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
    public WorkflowTransitionBehavior(
        IWorkflowEngine workflowEngine,
        IWorkflowDefinitionProvider definitions,
        IWorkflowStateStore stateStore,
        ICurrentUserContext? userContext = null)
    {
        _workflowEngine = workflowEngine ?? throw new ArgumentNullException(nameof(workflowEngine));
        _definitions = definitions ?? throw new ArgumentNullException(nameof(definitions));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _userContext = userContext;
    }

    /// <summary>Maximum choice-node hops before the routing chain is treated as a cycle.</summary>
    private const int MaxChoiceDepth = 5;

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
        var userRoles = _userContext?.User is ClaimsPrincipal principal
            ? principal.Claims.Where(c => c.Type == ClaimTypes.Role).Select(c => c.Value).ToList()
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

        // 6. Guard conditions, evaluated against the request first and then the entity.
        foreach (var condition in transitionConfig.Conditions ?? new List<WorkflowConditionConfig>())
        {
            var passed = _workflowEngine.EvaluateCondition(condition.Property, condition.Operator, condition.Value, request)
                || _workflowEngine.EvaluateCondition(condition.Property, condition.Operator, condition.Value, entity);

            if (!passed)
            {
                throw new WorkflowException(
                    $"Guard condition failed: {condition.Property} must satisfy operator "
                    + $"'{condition.Operator}' with value '{condition.Value}'.");
            }
        }

        // 7. Automated actions. A failed action fails the transition.
        var actionDetails = new List<ActionExecutionDetail>();
        foreach (var action in transitionConfig.Actions ?? new List<WorkflowActionConfig>())
        {
            var detail = await _workflowEngine.ExecuteActionAsync(
                action.Type, action.RequestType, action.PayloadTemplate,
                action.Method, action.Url, action.Headers, action.BodyTemplate, request, ct);

            actionDetails.Add(detail);

            if (!detail.Success)
            {
                throw new WorkflowException(
                    $"Workflow action execution failed on '{detail.ActionName}': {detail.ResponseBody}");
            }
        }

        // 8. Resolve the final state, following choice nodes.
        var resolvedTargetState = ResolveTargetState(workflowConfig, transitionRequest.ToState, request, entity);

        entity.CurrentState = resolvedTargetState;
        entity.WorkflowId = workflowConfig.Id;
        entity.WorkflowVersion = workflowConfig.Version;

        await _stateStore.SaveAsync(transitionRequest.EntityType, entity, ct);

        // 9. Run the handler, then record what actually happened.
        //
        // The activity log used to be written before the handler ran, with Success hardcoded to true.
        // A handler that threw left a history entry claiming the transition had succeeded -- a false
        // record in the one place someone would look to find out. It is now written after the handler
        // returns, and a failure is recorded as a failure before the exception propagates.
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
                var branchPassed = (branch.Conditions ?? new List<WorkflowConditionConfig>()).All(condition =>
                    _workflowEngine.EvaluateCondition(condition.Property, condition.Operator, condition.Value, request)
                    || _workflowEngine.EvaluateCondition(condition.Property, condition.Operator, condition.Value, entity));

                if (branchPassed)
                {
                    resolved = branch.ToState;
                    matched = true;
                    break;
                }
            }

            if (!matched) resolved = choiceNode.DefaultState;
        }

        return resolved;
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
            PayloadDetails = JsonSerializer.Serialize(request),
            Success = success,
            ErrorMessage = errorMessage,
            ExecutedActions = actionDetails
        };

        await _stateStore.AppendActivityLogAsync(log, ct);
    }
}
