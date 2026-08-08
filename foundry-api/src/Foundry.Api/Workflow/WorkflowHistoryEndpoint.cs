using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Foundry.Api.Manifest;
using Foundry.Api.Security;
using Foundry.Rules;

namespace Foundry.Api.Workflow;

/// <summary>
/// Serves the transition history of a workflow-bearing entity.
/// </summary>
/// <remarks>
/// <para>
/// <c>AppendActivityLogAsync</c> wrote an entry for every transition — who triggered it, when, from
/// which state to which, and the outcome of every automated action — and nothing served it. For a
/// regulated buyer the audit trail is the point, so a record that can be written and not read is half
/// a feature.
/// </para>
/// <para>
/// Mapped from the manifest at startup rather than source-generated, for the same reason
/// <c>MapDocsEndpoint</c> is: it needs no per-entity code, only the list of entities that have a
/// workflow.
/// </para>
/// </remarks>
public static class WorkflowHistoryEndpoint
{
    /// <summary>Maps <c>GET {entity-route}/{id}/history</c> for every entity with a workflow.</summary>
    public static IEndpointRouteBuilder MapWorkflowHistory(
        this IEndpointRouteBuilder endpoints, ApiManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        foreach (var workflow in manifest.Workflows ?? new List<WorkflowConfig>())
        {
            if (string.IsNullOrWhiteSpace(workflow.Entity)) continue;

            var endpoint = manifest.Endpoints?
                .FirstOrDefault(e => string.Equals(e.Entity, workflow.Entity, StringComparison.OrdinalIgnoreCase));

            // No REST surface for the entity means no route to hang the history off. Skipping is
            // correct -- there is nothing to read it relative to -- but it is worth knowing the
            // workflow is unreachable this way, so it is not silent.
            if (endpoint is null) continue;

            MapFor(endpoints, workflow.Entity, endpoint);
        }

        return endpoints;
    }

    private static void MapFor(IEndpointRouteBuilder endpoints, string entityName, EndpointConfig endpoint)
    {
        var route = $"{endpoint.Route.TrimEnd('/')}/{{id}}/history";

        // [FromServices] is explicit rather than inferred: a GET handler taking an un-attributed
        // interface makes minimal APIs infer a *body* parameter, and the route then throws at first
        // request with "Body was inferred but the method does not allow inferred body parameters".
        var builder = endpoints.MapGet(route, async (
            string id,
            HttpContext context,
            [Microsoft.AspNetCore.Mvc.FromServices] IWorkflowStateStore store) =>
        {
            // The entity is loaded first, and the history is only served if that succeeded.
            //
            // This is what keeps history inside the same isolation as the record. WorkflowActivityLog
            // is not IMultiTenant and carries no owner, so querying it directly by entity id would be
            // a read path beside the generated endpoints with none of their tenant or owner filtering
            // -- the defect already found twice in this codebase, in the archive reader and in the
            // GraphQL resolver. Loading through the repository applies those filters, so a caller who
            // cannot see the record cannot see what happened to it either.
            var entity = await store.LoadAsync(entityName, id, context.RequestAborted);
            if (entity is null) return Results.NotFound();

            var history = await store.ReadActivityLogAsync(entityName, id, context.RequestAborted);

            return Results.Ok(history.Select(WorkflowHistoryEntry.From).ToList());
        });

        // Reading a record's history is a read of that record, so it is governed by the roles the
        // entity declares for reading one. Inventing a separate declaration would give an entity two
        // answers to "who may see this", which is how the two transports came to disagree elsewhere.
        var roles = endpoint.Roles is not null
            && endpoint.Roles.TryGetValue("GET_BY_ID", out var declared)
            && declared is { Count: > 0 }
                ? declared
                : null;

        if (roles is not null)
        {
            builder.RequireAuthorization(new Microsoft.AspNetCore.Authorization.AuthorizeAttribute
            {
                Roles = string.Join(",", roles)
            });
        }
        else
        {
            // "No policy stated" is not "open to anyone" -- the same default the generated endpoints
            // apply.
            builder.RequireAuthorization();
        }

        builder.WithTags(entityName).WithName($"Get{entityName}WorkflowHistory");
    }
}

/// <summary>One transition, as the history endpoint returns it.</summary>
/// <remarks>
/// A projection rather than the stored record: <c>WorkflowActivityLog</c> is a
/// <c>BaseEntity&lt;ObjectId&gt;</c>, and returning it would put the log's own storage id, version
/// and audit timestamps into an API contract where they mean nothing to the caller.
/// </remarks>
public sealed record WorkflowHistoryEntry
{
    /// <summary>The workflow this transition belongs to.</summary>
    public required string WorkflowId { get; init; }

    /// <summary>The version of the workflow that was applied.</summary>
    public required string WorkflowVersion { get; init; }

    /// <summary>The transition that ran.</summary>
    public required string TransitionId { get; init; }

    /// <summary>The state the entity was in.</summary>
    public required string FromState { get; init; }

    /// <summary>The state the entity moved to.</summary>
    public required string ToState { get; init; }

    /// <summary>Who triggered it.</summary>
    public required string TriggeredBy { get; init; }

    /// <summary>When, in UTC.</summary>
    public required DateTime TriggeredAt { get; init; }

    /// <summary>Whether the transition completed.</summary>
    public required bool Success { get; init; }

    /// <summary>Why it did not, when it did not.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>The automated actions that ran as part of it.</summary>
    public required IReadOnlyList<WorkflowHistoryAction> Actions { get; init; }

    internal static WorkflowHistoryEntry From(WorkflowActivityLog log) => new()
    {
        WorkflowId = log.WorkflowId,
        WorkflowVersion = log.WorkflowVersion,
        TransitionId = log.TransitionId,
        FromState = log.FromState,
        ToState = log.ToState,
        TriggeredBy = log.TriggeredBy,
        TriggeredAt = log.TriggeredAt,
        Success = log.Success,
        ErrorMessage = log.ErrorMessage,
        Actions = log.ExecutedActions.Select(WorkflowHistoryAction.From).ToList()
    };
}

/// <summary>One automated action within a transition.</summary>
public sealed record WorkflowHistoryAction
{
    /// <summary>The kind of action, e.g. InternalApi or ExternalApi.</summary>
    public required string ActionType { get; init; }

    /// <summary>The command or URL it targeted.</summary>
    public required string ActionName { get; init; }

    /// <summary>Whether it succeeded.</summary>
    public required bool Success { get; init; }

    /// <summary>The status it reported.</summary>
    public required int StatusCode { get; init; }

    /// <summary>The attempt number (1-indexed).</summary>
    public int AttemptNumber { get; init; }

    /// <summary>Whether this action is executing as compensation.</summary>
    public bool IsCompensation { get; init; }

    /// <summary>The name of the action being compensated, if any.</summary>
    public string? CompensatesActionName { get; init; }

    internal static WorkflowHistoryAction From(ActionExecutionDetail detail) => new()
    {
        ActionType = detail.ActionType,
        ActionName = detail.ActionName,
        Success = detail.Success,
        StatusCode = detail.StatusCode,
        AttemptNumber = detail.AttemptNumber,
        IsCompensation = detail.IsCompensation,
        CompensatesActionName = detail.CompensatesActionName

        // ResponseBody is deliberately not projected. It is whatever an external system returned, of
        // unbounded size and unreviewed content, and an action can target any URL the workflow names
        // -- so echoing it into an API response would relay an arbitrary third party's output to a
        // caller who is authorized to read this entity and nothing else.
    };
}
