using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using MongoDB.Bson;
using Foundry.Core.User;

namespace Foundry.Rules;

/// <summary>
/// MediatR pipeline behavior that transparently processes state machine transitions.
/// </summary>
/// <remarks>
/// <para>
/// Every collaborator is injected. This class previously located the API manifest, the current-user
/// context, the entity's CLR type and <c>IRepository&lt;T&gt;</c> by scanning
/// <c>AppDomain.CurrentDomain.GetAssemblies()</c> for simple-name matches and invoking methods through
/// <c>MethodInfo</c>. That made the orchestration untestable without a real database and the API
/// assembly loaded in-process, and it failed in ways the compiler could not see — see
/// <see cref="IWorkflowStateStore"/> for the specific failure modes.
/// </para>
/// <para>
/// The transition sequence is unchanged: resolve the workflow, check temporal validity, authorise,
/// load the entity, verify the source state, evaluate guards, run actions, resolve choice nodes, then
/// persist and record.
/// </para>
/// </remarks>
public class WorkflowTransitionBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private readonly IWorkflowEngine _workflowEngine;
    private readonly IWorkflowDefinitionProvider _definitions;
    private readonly IWorkflowStateStore _stateStore;
    private readonly ICurrentUserContext? _userContext;

    /// <summary>Delay function, injectable purely so tests can skip real wall-clock delays during retry backoff.</summary>
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <summary>Maximum choice-node hops before the routing chain is treated as a cycle.</summary>
    private const int MaxChoiceDepth = 5;

    /// <summary>Maximum retry attempts for an action that is configured as retryable.</summary>
    private const int MaxActionAttempts = 3;

    /// <summary>
    /// Initializes a new instance of the <see cref="WorkflowTransitionBehavior{TRequest, TResponse}"/> class.
    /// </summary>
    /// <param name="workflowEngine">Evaluates guard conditions, permissions and actions.</param>
    /// <param name="definitions">Supplies the configured workflow definitions.</param>
    /// <param name="stateStore">Loads and persists the workflow-bearing entity.</param>
    /// <param name="userContext">
    /// The caller's identity. Optional: a background or system-initiated transition has no user, and
    /// requiring one would make the workflow unusable outside a request.
    /// </param>
    /// <param name="delay">Optional delay function for retries. Defaults to Task.Delay for production use.</param>
    public WorkflowTransitionBehavior(
        IWorkflowEngine workflowEngine,
        IWorkflowDefinitionProvider definitions,
        IWorkflowStateStore stateStore,
        ICurrentUserContext? userContext = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _workflowEngine = workflowEngine ?? throw new ArgumentNullException(nameof(workflowEngine));
        _definitions = definitions ?? throw new ArgumentNullException(nameof(definitions));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _userContext = userContext;
        _delay = delay ?? Task.Delay;
    }

    /// <inheritdoc />
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken ct)
    {
        if (request is not IWorkflowTransitionRequest transitionRequest)
        {
            return await next();
        }

        // 1. Resolve the active workflow for this entity.
        var workflowConfig = _definitions.GetWorkflows()
            .FirstOrDefault(w => w.Entity.Equals(transitionRequest.EntityType, StringComparison.OrdinalIgnoreCase) && w.IsActive)
            ?? throw new WorkflowException(
                $"No active workflow definition found for entity '{transitionRequest.EntityType}'.");

        // 2. Temporal validity.
        var now = DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(workflowConfig.EffectiveDate)
            && DateTime.TryParse(workflowConfig.EffectiveDate, out var effectiveDate) && now < effectiveDate)
        {
            throw new WorkflowException(
                $"Workflow '{workflowConfig.Name}' is not yet effective (starts on {workflowConfig.EffectiveDate}).");
        }
        if (!string.IsNullOrWhiteSpace(workflowConfig.ExpirationDate)
            && DateTime.TryParse(workflowConfig.ExpirationDate, out var expirationDate) && now > expirationDate)
        {
            throw new WorkflowException(
                $"Workflow '{workflowConfig.Name}' expired on {workflowConfig.ExpirationDate}.");
        }

        // 3. Transition definition.
        var transitionConfig = workflowConfig.Transitions?
            .FirstOrDefault(t => t.Id.Equals(transitionRequest.TransitionId, StringComparison.OrdinalIgnoreCase))
            ?? throw new WorkflowException(
                $"Transition '{transitionRequest.TransitionId}' is not defined in workflow '{workflowConfig.Name}'.");

        // 4. Caller identity and authorisation.
        var operatorId = _userContext?.OperatorId ?? "system";

        // Role claims are read under both the raw name and the WS-Federation URI. Only the URI was
        // matched, and the framework's own authentication sets MapInboundClaims=false with a
        // configurable RoleClaimType -- so a caller whose token said `"role": "Approver"` arrived
        // here with no roles at all, and every role-gated transition was refused. Silent in the safe
        // direction, and still wrong: it makes a correctly-configured workflow look broken.
        var userRoles = _userContext?.User is ClaimsPrincipal principal
            ? principal.Claims
                .Where(c => c.Type == ClaimTypes.Role || c.Type == "role" || c.Type == "roles")
                .Select(c => c.Value)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
            : new List<string>();

        var fromStateConfig = workflowConfig.States?
            .FirstOrDefault(s => s.Name.Equals(transitionRequest.FromState, StringComparison.OrdinalIgnoreCase));

        _workflowEngine.ValidatePermission(
            transitionRequest.TransitionId,
            transitionRequest.FromState,
            transitionConfig.RequiredRoles,
            fromStateConfig?.AllowedRoles ?? new List<string>(),
            userRoles);

        // 5. Load the entity and verify its current state.
        var entity = await _stateStore.LoadAsync(transitionRequest.EntityType, transitionRequest.EntityId, ct)
            ?? throw new WorkflowException(
                $"Target entity '{transitionRequest.EntityType}' with ID '{transitionRequest.EntityId}' does not exist.");

        if (string.IsNullOrWhiteSpace(entity.CurrentState))
        {
            // Unset state means the entity has not entered the workflow yet; adopt the initial state.
            entity.CurrentState = workflowConfig.States?.FirstOrDefault(s => s.IsInitial)?.Name
                ?? transitionRequest.FromState;
        }

        if (!entity.CurrentState.Equals(transitionRequest.FromState, StringComparison.OrdinalIgnoreCase))
        {
            throw new WorkflowException(
                $"Invalid transition source state. Entity is currently in state '{entity.CurrentState}', "
                + $"but this transition requires state '{transitionRequest.FromState}'.");
        }

        // 6. Guard conditions, each evaluated against the one object it names.
        //
        // This used to evaluate every guard against the request and then the entity, passing if
        // *either* satisfied it. The request is the MediatR command bound from the caller's body, so
        // a guard about a value the server owns could be answered with a value the caller chose: a
        // guard reading the order's TotalAmount was equally satisfied by a command carrying its own
        // TotalAmount. Transition commands are partial records and guards on request payloads are a
        // supported feature, so that collision is an ordinary design rather than a contrived one.
        foreach (var condition in transitionConfig.Conditions ?? new List<WorkflowConditionConfig>())
        {
            if (!Evaluate(condition, request, entity))
            {
                throw new WorkflowException(
                    $"Guard condition failed: {condition.Source} property {condition.Property} must "
                    + $"satisfy operator '{condition.Operator}' with value '{condition.Value}'.");
            }
        }

        // 7. Resolve the final state, following choice nodes.
        var resolvedTargetState = ResolveTargetState(workflowConfig, transitionRequest.ToState, request, entity);

        var actionDetails = new List<ActionExecutionDetail>();

        // 8. Run the handler, then the automated actions, then persist, then record what happened.
        //
        // The order of these three has been wrong twice, in the same direction each time: recording
        // or committing something before knowing whether it happened.
        //
        // The activity log used to be written before the handler ran, with Success hardcoded to true,
        // so a handler that threw left a history entry claiming success. That was fixed. The state
        // write was not: the entity was moved to its new state and saved *before* the handler ran, so
        // a handler that threw left the record advanced while the log correctly recorded a failure --
        // an order sitting in Approved with its own history saying the approval failed.
        //
        // The handler now runs first. A transition that fails leaves the entity where it was.
        //
        // The external actions moved with it, and for a stronger reason. They used to run before the
        // handler, so a transition the handler went on to reject had already charged a card or sent a
        // notification -- effects outside this process that nothing here can take back. Running them
        // after the handler means the cheap, local, reversible check happens before the expensive,
        // remote, irreversible one.
        //
        // What remains is genuinely hard: an action that fails after an earlier action succeeded
        // leaves that earlier effect in place, and no compensation is attempted. Every executed
        // action is recorded in the activity log including on the failure path, so what happened is
        // at least knowable. A saga would be the real answer and is not what this is.
        TResponse response;
        try
        {
            response = await next();
        }
        catch (Exception ex)
        {
            await AppendLogAsync(transitionRequest, workflowConfig, resolvedTargetState, operatorId,
                request, actionDetails, success: false, ex.Message, ct);
            throw;
        }

        // Track succeeded actions for potential compensation: (original action config, execution detail)
        var succeededActions = new List<(WorkflowActionConfig Action, ActionExecutionDetail Detail)>();

        // Determine the action that failed, if any, for error reporting
        ActionExecutionDetail? failedActionDetail = null;

        foreach (var action in transitionConfig.Actions ?? new List<WorkflowActionConfig>())
        {
            int attemptsRemaining = action.Retryable ? MaxActionAttempts : 1;
            bool lastAttemptSucceeded = false;

            for (int attemptNumber = 1; attemptNumber <= MaxActionAttempts && attemptsRemaining > 0; attemptNumber++)
            {
                var detail = await _workflowEngine.ExecuteActionAsync(
                    action.Type, action.RequestType, action.PayloadTemplate,
                    action.Method, action.Url, action.Headers, action.BodyTemplate, request, ct);

                detail = detail with { AttemptNumber = attemptNumber };
                actionDetails.Add(detail);

                if (detail.Success)
                {
                    lastAttemptSucceeded = true;

                    // Store succeeded action for potential compensation
                    succeededActions.Add((action, detail));
                    break; // Exit retry loop on success
                }

                failedActionDetail = detail;
                attemptsRemaining--;
                if (attemptsRemaining > 0)
                {
                    var delay = TimeSpan.FromSeconds(Math.Min(30, 2 * Math.Pow(2, attemptNumber)));
                    await _delay(delay, ct);
                }
            }

            if (!lastAttemptSucceeded)
            {
                // Execute compensation for all previously succeeded actions in reverse order
                foreach (var (compensatedAction, compensatedDetail) in succeededActions.AsEnumerable().Reverse())
                {
                    var compensateConfig = compensatedAction.CompensateWith;
                    if (compensateConfig != null)
                    {
                        var compensationDetail = await _workflowEngine.ExecuteActionAsync(
                            compensateConfig.Type,
                            compensateConfig.RequestType,
                            compensateConfig.PayloadTemplate,
                            compensateConfig.Method,
                            compensateConfig.Url,
                            compensateConfig.Headers,
                            compensateConfig.BodyTemplate,
                            request,
                            ct);

                        compensationDetail = compensationDetail with
                        {
                            IsCompensation = true,
                            CompensatesActionName = compensatedDetail.ActionName
                        };

                        actionDetails.Add(compensationDetail);
                    }
                }

                await AppendLogAsync(transitionRequest, workflowConfig, resolvedTargetState, operatorId,
                    request, actionDetails, success: false, failedActionDetail?.ResponseBody, ct);

                throw new WorkflowException(
                    $"Workflow action execution failed on '{failedActionDetail?.ActionName}': {failedActionDetail?.ResponseBody}");
            }
        }

        entity.CurrentState = resolvedTargetState;
        entity.WorkflowId = workflowConfig.Id;
        entity.WorkflowVersion = workflowConfig.Version;

        await _stateStore.SaveAsync(transitionRequest.EntityType, entity, ct);

        await AppendLogAsync(transitionRequest, workflowConfig, resolvedTargetState, operatorId,
            request, actionDetails, success: true, errorMessage: null, ct);

        return response;
    }

    /// <summary>
    /// Follows choice nodes from the requested target state to a concrete state.
    /// </summary>
    /// <remarks>
    /// Bounded by <see cref="MaxChoiceDepth"/>. A chain that is still unresolved at the limit throws
    /// rather than silently leaving the entity in a choice node's id as though it were a real state,
    /// which would leave a document in a state no transition matches.
    /// </remarks>
    private string ResolveTargetState(
        WorkflowConfig workflowConfig, string requestedState, object request, IWorkflowStateful entity)
    {
        var resolved = requestedState;

        for (var depth = 0; depth <= MaxChoiceDepth; depth++)
        {
            var choiceNode = workflowConfig.ChoiceNodes?.FirstOrDefault(c =>
                c.Id.Equals(resolved, StringComparison.OrdinalIgnoreCase)
                || c.Name.Equals(resolved, StringComparison.OrdinalIgnoreCase));

            if (choiceNode == null) return resolved;

            if (depth == MaxChoiceDepth)
            {
                throw new WorkflowException(
                    $"Choice node routing for '{requestedState}' did not resolve to a state within "
                    + $"{MaxChoiceDepth} hops; the definition may contain a cycle.");
            }

            var matched = false;
            foreach (var branch in choiceNode.Branches ?? new List<WorkflowChoiceBranchConfig>())
            {
                // Same rule as a transition guard, and for the same reason: routing decided by an
                // either-source fallback let a caller choose which state they landed in.
                var branchPassed = (branch.Conditions ?? new List<WorkflowConditionConfig>())
                    .All(condition => Evaluate(condition, request, entity));

                if (branchPassed)
                {
                    resolved = branch.ToState;
                    matched = true;
                    break;
                }
            }

            if (matched) continue;

            // No branch held and no fallback declared, so there is nowhere to route.
            //
            // This assigned DefaultState regardless -- an empty string when none was declared, which
            // the manifest emitter hardcoded for every gate -- and saved it. The record landed in a
            // state no transition matches: unreachable, invisible to every state-based query, behind
            // a 200 and a history entry naming "". The comment beside the emitter said an unmatched
            // gate was "a routing failure rather than guessing"; nothing implemented that until now.
            if (string.IsNullOrWhiteSpace(choiceNode.DefaultState))
            {
                throw new WorkflowException(
                    $"No branch of decision gate '{choiceNode.Name}' ({choiceNode.Id}) matched, and it "
                    + "declares no default state, so the transition has nowhere to route. Add a "
                    + "'defaultState' to the gate, or a branch covering this case.");
            }

            resolved = choiceNode.DefaultState;
        }

        return resolved;
    }

    /// <summary>
    /// Evaluates one guard against the single object it names.
    /// </summary>
    /// <remarks>
    /// The one place the source is decided, so a transition guard and a choice-node branch cannot
    /// disagree about what "this condition reads" means.
    /// </remarks>
    private bool Evaluate(WorkflowConditionConfig condition, object request, IWorkflowStateful entity)
    {
        var target = condition.ReadsRequest ? request : (object)entity;
        return _workflowEngine.EvaluateCondition(
            condition.Property, condition.Operator, condition.Value, target);
    }

    private async Task AppendLogAsync(
        IWorkflowTransitionRequest transitionRequest,
        WorkflowConfig workflowConfig,
        string resolvedTargetState,
        string operatorId,
        object request,
        List<ActionExecutionDetail> actionDetails,
        bool success,
        string? errorMessage,
        CancellationToken ct)
    {
        var log = new WorkflowActivityLog
        {
            Id = ObjectId.GenerateNewId(),
            EntityId = transitionRequest.EntityId,
            EntityType = transitionRequest.EntityType,
            WorkflowId = workflowConfig.Id,
            WorkflowVersion = workflowConfig.Version,
            FromState = transitionRequest.FromState,
            ToState = resolvedTargetState,
            TransitionId = transitionRequest.TransitionId,
            TriggeredBy = operatorId,
            TriggeredAt = DateTime.UtcNow,
            // Declared-sensitive values are withheld: the entity encrypts and masks them,
            // and the log used to store the same values beside it in clear text.
            PayloadDetails = WorkflowPayloadRedactor.Serialize(request),
            Success = success,
            ErrorMessage = errorMessage,
            ExecutedActions = actionDetails
        };

        await _stateStore.AppendActivityLogAsync(log, ct);
    }
}
