using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundry.Api.MediatR;
using Foundry.Core.Entities;
using Foundry.Rules;
using MediatR;
using MongoDB.Bson;

namespace Foundry.Api.Workflow;

/// <summary>
/// Keeps an entity's workflow fields out of a client's hands: only a transition moves
/// <see cref="IWorkflowStateful.CurrentState"/>.
/// </summary>
/// <remarks>
/// <para>
/// A PUT is a whole-document replace, and the repository kept the server's <c>CreatedAtUtc</c> but
/// took <c>CurrentState</c>, <c>WorkflowId</c> and <c>WorkflowVersion</c> from the body. So a PUT
/// could set any state directly, skipping the transition, its <c>requiredRoles</c> and its actions.
/// Reproduced against a live API: a <c>TeamMember</c>, who may edit their own timesheet but not
/// approve it, set <c>"CurrentState": "Approved"</c> on a draft and got 200. A POST could do the
/// same, and one that sent no state stored <c>""</c>, which the workflow adopts as initial on the
/// first transition but which a <c>?currentState=Draft</c> filter never matches.
/// </para>
/// <para>
/// Here, rather than in the repository, because the workflow engine saves through the repository
/// too and must be able to change the state. It saves through <see cref="IWorkflowStateStore"/>,
/// never through <see cref="InsertCommand{TEntity}"/> or <see cref="UpdateCommand{TEntity}"/>, so
/// everything this sees is a client write.
/// </para>
/// </remarks>
public sealed class WorkflowFieldGuardBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    private readonly IWorkflowDefinitionProvider _definitions;
    private readonly IWorkflowStateStore _stateStore;

    public WorkflowFieldGuardBehavior(IWorkflowDefinitionProvider definitions, IWorkflowStateStore stateStore)
    {
        _definitions = definitions ?? throw new ArgumentNullException(nameof(definitions));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
    }

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        if (request is not IClientEntityWrite write || write.Entity is not IWorkflowStateful entity)
            return await next();

        var entityType = write.Entity.GetType().Name;

        if (write.IsInsert)
        {
            var workflow = _definitions.GetWorkflows()
                .FirstOrDefault(w => w.Entity.Equals(entityType, StringComparison.OrdinalIgnoreCase) && w.IsActive);

            // No active workflow: leave the row outside one, as the transition behaviour would find it.
            entity.CurrentState = workflow?.States?.FirstOrDefault(s => s.IsInitial)?.Name ?? string.Empty;
            entity.WorkflowId = workflow?.Id ?? string.Empty;
            entity.WorkflowVersion = workflow?.Version ?? string.Empty;
        }
        else
        {
            // Read in the caller's scope, so tenant and owner filters apply. A row they cannot see
            // comes back null; the update itself then fails as it would have, and the fields are
            // blanked rather than left as the caller sent them.
            var id = write.Entity is IEntity<ObjectId> keyed ? keyed.Id.ToString() : string.Empty;
            var stored = await _stateStore.LoadAsync(entityType, id, cancellationToken);

            entity.CurrentState = stored?.CurrentState ?? string.Empty;
            entity.WorkflowId = stored?.WorkflowId ?? string.Empty;
            entity.WorkflowVersion = stored?.WorkflowVersion ?? string.Empty;
        }

        return await next();
    }
}
