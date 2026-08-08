using System;
using System.Collections.Generic;
using MongoDB.Bson;
using Foundry.Core.Entities;

namespace Foundry.Rules;

/// <summary>
/// Represents a historical trace of state changes and action execution logs inside the workflow engine.
/// </summary>
public record WorkflowActivityLog : BaseEntity<ObjectId>
{
    /// <summary>
    /// Gets or sets the target entity's unique ID.
    /// </summary>
    public required string EntityId { get; init; }

    /// <summary>
    /// Gets or sets the name/type of the target entity.
    /// </summary>
    public required string EntityType { get; init; }

    /// <summary>
    /// Gets or sets the workflow definition ID.
    /// </summary>
    public required string WorkflowId { get; init; }

    /// <summary>
    /// Gets or sets the version of the workflow applied.
    /// </summary>
    public required string WorkflowVersion { get; init; }

    /// <summary>
    /// Gets or sets the state name transitioned from.
    /// </summary>
    public required string FromState { get; init; }

    /// <summary>
    /// Gets or sets the state name transitioned to.
    /// </summary>
    public required string ToState { get; init; }

    /// <summary>
    /// Gets or sets the name/identifier of the transition.
    /// </summary>
    public required string TransitionId { get; init; }

    /// <summary>
    /// Gets or sets the operator ID who triggered the transition.
    /// </summary>
    public required string TriggeredBy { get; init; }

    /// <summary>
    /// Gets or sets the UTC timestamp when the transition occurred.
    /// </summary>
    public required DateTime TriggeredAt { get; init; }

    /// <summary>
    /// Gets or sets the serialized request payload properties details.
    /// </summary>
    public required string PayloadDetails { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether the state transition was executed successfully.
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    /// Gets or sets potential execution error messages.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Gets or sets the list of executed automated DAG actions.
    /// </summary>
    public List<ActionExecutionDetail> ExecutedActions { get; init; } = new();
}

/// <summary>
/// Represents execution trace details for an automated action.
/// </summary>
public record ActionExecutionDetail
{
    /// <summary>
    /// Gets or sets the action type (e.g. InternalApi, ExternalApi).
    /// </summary>
    public required string ActionType { get; init; }

    /// <summary>
    /// Gets or sets the target command or URL path.
    /// </summary>
    public required string ActionName { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether the action ran successfully.
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    /// Gets or sets status response code (e.g. 200 or 500).
    /// </summary>
    public required int StatusCode { get; init; }

    /// <summary>
    /// Gets or sets body response text if any.
    /// </summary>
    public string? ResponseBody { get; init; }

    /// <summary>
    /// Gets or sets the attempt number (1-indexed).
    /// </summary>
    public int AttemptNumber { get; init; } = 1;

    /// <summary>
    /// Gets or sets a value indicating whether this action is executing as compensation.
    /// </summary>
    public bool IsCompensation { get; init; }

    /// <summary>
    /// Gets or sets the name of the action being compensated, if any.
    /// </summary>
    public string? CompensatesActionName { get; init; }
}
