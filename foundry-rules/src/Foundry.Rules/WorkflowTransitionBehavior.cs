using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Microsoft.Extensions.DependencyInjection;


namespace Foundry.Rules;

/// <summary>
/// MediatR pipeline behavior that transparently processes state machine transitions.
/// </summary>
public class WorkflowTransitionBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IWorkflowEngine _workflowEngine;

    /// <summary>
    /// Initializes a new instance of the <see cref="WorkflowTransitionBehavior{TRequest, TResponse}"/> class.
    /// </summary>
    /// <param name="serviceProvider">The application's service provider container.</param>
    /// <param name="workflowEngine">The workflow execution engine instance.</param>
    public WorkflowTransitionBehavior(IServiceProvider serviceProvider, IWorkflowEngine workflowEngine)
    {
        _serviceProvider = serviceProvider;
        _workflowEngine = workflowEngine;
    }

    /// <inheritdoc />
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken ct)
    {
        if (request is not IWorkflowTransitionRequest transitionRequest)
        {
            return await next();
        }

        // 1. Resolve manifest configurations
        object? manifest = null;
        var manifestType = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => {
                try { return a.GetTypes(); } catch { return Array.Empty<Type>(); }
            })
            .FirstOrDefault(t => t.Name == "ApiManifest" || t.FullName == "Foundry.Api.Manifest.ApiManifest");

        if (manifestType == null)
        {
            throw new WorkflowException("ApiManifest type could not be resolved from loaded assemblies.");
        }

        manifest = _serviceProvider.GetService(manifestType);

        if (manifest == null)
        {
            throw new WorkflowException("ApiManifest is not registered in the service container.");
        }

        var workflowsProp = manifestType.GetProperty("Workflows");
        var workflows = workflowsProp?.GetValue(manifest) as IEnumerable<WorkflowConfig>;

        // 2. Find active workflow definition for the target entity
        var workflowConfig = workflows?.FirstOrDefault(w => 
            w.Entity.Equals(transitionRequest.EntityType, StringComparison.OrdinalIgnoreCase) && w.IsActive);

        if (workflowConfig == null)
        {
            throw new WorkflowException($"No active workflow definition found for entity '{transitionRequest.EntityType}'.");
        }

        // Validate temporal expiration checks
        var now = DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(workflowConfig.EffectiveDate) && DateTime.TryParse(workflowConfig.EffectiveDate, out var effDate) && now < effDate)
        {
            throw new WorkflowException($"Workflow '{workflowConfig.Name}' is not yet effective (Starts on {workflowConfig.EffectiveDate}).");
        }
        if (!string.IsNullOrWhiteSpace(workflowConfig.ExpirationDate) && DateTime.TryParse(workflowConfig.ExpirationDate, out var expDate) && now > expDate)
        {
            throw new WorkflowException($"Workflow '{workflowConfig.Name}' has expired on {workflowConfig.ExpirationDate}.");
        }

        // 3. Find Transition configuration mapping
        var transitionConfig = workflowConfig.Transitions?.FirstOrDefault(t => 
            t.Id.Equals(transitionRequest.TransitionId, StringComparison.OrdinalIgnoreCase));

        if (transitionConfig == null)
        {
            throw new WorkflowException($"Transition '{transitionRequest.TransitionId}' is not defined in workflow '{workflowConfig.Name}'.");
        }

        // 4. Resolve current user context and validate permission matrix
        List<string> userRoles = new();
        string operatorId = "system";

        // Dynamically resolve ICurrentUserContext (if registered in API host)
        var userContextType = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => {
                try { return a.GetTypes(); } catch { return Array.Empty<Type>(); }
            })
            .FirstOrDefault(t => t.Name.Equals("ICurrentUserContext", StringComparison.OrdinalIgnoreCase) || t.FullName?.Equals("Foundry.Core.User.ICurrentUserContext", StringComparison.OrdinalIgnoreCase) == true);

        if (userContextType != null)
        {
            var userContext = _serviceProvider.GetService(userContextType);
            if (userContext != null)
            {
                var operatorProp = userContextType.GetProperty("OperatorId");
                if (operatorProp != null)
                {
                    operatorId = operatorProp.GetValue(userContext)?.ToString() ?? "anonymous";
                }

                var userProp = userContextType.GetProperty("User");
                if (userProp != null && userProp.GetValue(userContext) is ClaimsPrincipal principal)
                {
                    userRoles = principal.Claims.Where(c => c.Type == ClaimTypes.Role).Select(c => c.Value).ToList();
                }
            }
        }

        var fromStateConfig = workflowConfig.States?.FirstOrDefault(s => s.Name.Equals(transitionRequest.FromState, StringComparison.OrdinalIgnoreCase));
        var fromStateRoles = fromStateConfig?.AllowedRoles ?? new List<string>();

        _workflowEngine.ValidatePermission(
            transitionRequest.TransitionId,
            transitionRequest.FromState,
            transitionConfig.RequiredRoles,
            fromStateRoles,
            userRoles);

        // 5. Resolve Target Entity from Repository and check state
        var allTypes = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => {
                try { return a.GetTypes(); } catch { return Array.Empty<Type>(); }
            })
            .ToList();

        var entityType = allTypes.FirstOrDefault(t => t.Name.Equals(transitionRequest.EntityType, StringComparison.OrdinalIgnoreCase));
        if (entityType == null)
        {
            throw new WorkflowException($"Entity type '{transitionRequest.EntityType}' could not be resolved in the domain assemblies.");
        }

        var repoType = allTypes.FirstOrDefault(t => t.Name.Equals("IRepository`1") && t.IsInterface && t.IsGenericType)?
            .MakeGenericType(entityType);

        if (repoType == null)
        {
            throw new WorkflowException($"Repository interface IRepository<{entityType.Name}> could not be resolved.");
        }

        var repo = _serviceProvider.GetService(repoType);
        if (repo == null)
        {
            throw new WorkflowException($"Repository service IRepository<{entityType.Name}> is not registered.");
        }

        var getByIdMethod = repoType.GetMethod("GetByIdAsync");
        if (getByIdMethod == null)
        {
            throw new WorkflowException("GetByIdAsync method is missing on the repository.");
        }

        var idType = transitionRequest.EntityId.Length == 24 ? typeof(MongoDB.Bson.ObjectId) : typeof(string);
        object parsedId = idType == typeof(MongoDB.Bson.ObjectId) 
            ? MongoDB.Bson.ObjectId.Parse(transitionRequest.EntityId) 
            : transitionRequest.EntityId;

        var entityTask = (Task)getByIdMethod.Invoke(repo, new object[] { parsedId, null!, ct })!;
        await entityTask;
        var entity = ((dynamic)entityTask).Result;

        if (entity == null)
        {
            throw new WorkflowException($"Target entity document with ID '{transitionRequest.EntityId}' does not exist.");
        }

        var statefulEntity = entity as IWorkflowStateful;
        if (statefulEntity == null)
        {
            throw new WorkflowException($"Entity type '{transitionRequest.EntityType}' must implement IWorkflowStateful to track state machine status.");
        }

        if (string.IsNullOrWhiteSpace(statefulEntity.CurrentState))
        {
            // Auto-initialize to initial state if unset
            var initialState = workflowConfig.States?.FirstOrDefault(s => s.IsInitial)?.Name ?? transitionRequest.FromState;
            statefulEntity.CurrentState = initialState;
        }

        if (!statefulEntity.CurrentState.Equals(transitionRequest.FromState, StringComparison.OrdinalIgnoreCase))
        {
            throw new WorkflowException($"Invalid transition source state. Entity is currently in state '{statefulEntity.CurrentState}', but this transition requires state '{transitionRequest.FromState}'.");
        }

        // 6. Evaluate guard conditions
        if (transitionConfig.Conditions != null)
        {
            foreach (var cond in transitionConfig.Conditions)
            {
                var passed = _workflowEngine.EvaluateCondition(cond.Property, cond.Operator, cond.Value, request);
                if (!passed && entity != null)
                {
                    passed = _workflowEngine.EvaluateCondition(cond.Property, cond.Operator, cond.Value, entity);
                }

                if (!passed)
                {
                    throw new WorkflowException($"Guard condition failed: {cond.Property} must satisfy operator '{cond.Operator}' with value '{cond.Value}'.");
                }
            }
        }

        // 7. Execute automated actions (DAG actions)
        var actionDetails = new List<ActionExecutionDetail>();
        if (transitionConfig.Actions != null)
        {
            foreach (var act in transitionConfig.Actions)
            {
                var detail = await _workflowEngine.ExecuteActionAsync(
                    act.Type,
                    act.RequestType,
                    act.PayloadTemplate,
                    act.Method,
                    act.Url,
                    act.Headers,
                    act.BodyTemplate,
                    request,
                    ct);

                actionDetails.Add(detail);
                if (!detail.Success)
                {
                    // Fail the transition if an action fails (participates in transactions)
                    throw new WorkflowException($"Workflow action execution failed on '{detail.ActionName}': {detail.ResponseBody}");
                }
            }
        }

        // 8. Resolve final target state (dynamic routing decision gates / choice nodes recursion)
        var resolvedTargetState = transitionRequest.ToState;
        var maxDepth = 5;
        var currentDepth = 0;

        while (currentDepth < maxDepth)
        {
            var choiceNode = workflowConfig.ChoiceNodes?.FirstOrDefault(c => 
                c.Id.Equals(resolvedTargetState, StringComparison.OrdinalIgnoreCase) ||
                c.Name.Equals(resolvedTargetState, StringComparison.OrdinalIgnoreCase));

            if (choiceNode == null)
            {
                break;
            }

            var matchedBranch = false;
            foreach (var branch in choiceNode.Branches ?? new List<WorkflowChoiceBranchConfig>())
            {
                var branchPassed = true;
                if (branch.Conditions != null)
                {
                    foreach (var cond in branch.Conditions)
                    {
                        var passed = _workflowEngine.EvaluateCondition(cond.Property, cond.Operator, cond.Value, request);
                        if (!passed && entity != null)
                        {
                            passed = _workflowEngine.EvaluateCondition(cond.Property, cond.Operator, cond.Value, entity);
                        }
                        if (!passed)
                        {
                            branchPassed = false;
                            break;
                        }
                    }
                }

                if (branchPassed)
                {
                    resolvedTargetState = branch.ToState;
                    matchedBranch = true;
                    break;
                }
            }

            if (!matchedBranch)
            {
                resolvedTargetState = choiceNode.DefaultState;
            }

            currentDepth++;
        }

        statefulEntity.CurrentState = resolvedTargetState;
        statefulEntity.WorkflowId = workflowConfig.Id;
        statefulEntity.WorkflowVersion = workflowConfig.Version;

        var updateMethod = repoType.GetMethod("UpdateAsync");
        if (updateMethod == null)
        {
            throw new WorkflowException("UpdateAsync method is missing on the repository.");
        }

        var updateTask = (Task)updateMethod.Invoke(repo, new object[] { entity!, null!, ct })!;
        await updateTask;

        // 9. Write Workflow Activity Log entry
        var logRepoType = allTypes.FirstOrDefault(t => t.Name.Equals("IRepository`1") && t.IsInterface && t.IsGenericType)?
            .MakeGenericType(typeof(WorkflowActivityLog));

        if (logRepoType != null)
        {
            var logRepo = _serviceProvider.GetService(logRepoType);
            if (logRepo != null)
            {
                var insertMethod = logRepoType.GetMethod("InsertAsync");
                if (insertMethod != null)
                {
                    var log = new WorkflowActivityLog
                    {
                        Id = MongoDB.Bson.ObjectId.GenerateNewId(),
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
                        Success = true,
                        ExecutedActions = actionDetails
                    };
                    await (Task)insertMethod.Invoke(logRepo, new object[] { log, null!, ct })!;
                }
            }
        }

        return await next();
    }
}
