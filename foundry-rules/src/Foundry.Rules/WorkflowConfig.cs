using System;
using System.Collections.Generic;

namespace Foundry.Rules;

/// <summary>
/// Represents a workflow definition manifest config.
/// </summary>
public class WorkflowConfig
{
    /// <summary>
    /// Gets or sets the workflow definition ID.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the name of the workflow.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the target entity class name.
    /// </summary>
    public string Entity { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the version string.
    /// </summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the ISO effective date string.
    /// </summary>
    public string EffectiveDate { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the ISO expiration date string.
    /// </summary>
    public string ExpirationDate { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether this workflow is active.
    /// </summary>
    public bool IsActive { get; set; }

    /// <summary>
    /// Gets or sets the collection of defined workflow states.
    /// </summary>
    public List<WorkflowStateConfig> States { get; set; } = new();

    /// <summary>
    /// Gets or sets the collection of state transition gates.
    /// </summary>
    public List<WorkflowTransitionConfig> Transitions { get; set; } = new();

    /// <summary>
    /// Gets or sets the collection of decision choice nodes (UML decision gates).
    /// </summary>
    public List<WorkflowChoiceNodeConfig> ChoiceNodes { get; set; } = new();
}

/// <summary>
/// Configuration for a workflow state block.
/// </summary>
public class WorkflowStateConfig
{
    /// <summary>
    /// Gets or sets the state name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether this is the initial starting state.
    /// </summary>
    public bool IsInitial { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this is a terminal state.
    /// </summary>
    public bool IsFinal { get; set; }

    /// <summary>
    /// Gets or sets the authorized roles list allowed in this state.
    /// </summary>
    public List<string> AllowedRoles { get; set; } = new();
}

/// <summary>
/// Configuration for a state transition edge.
/// </summary>
public class WorkflowTransitionConfig
{
    /// <summary>
    /// Gets or sets the unique transition ID.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the human-readable transition name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the source state name.
    /// </summary>
    public string FromState { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the target destination state name.
    /// </summary>
    public string ToState { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the MediatR trigger command name.
    /// </summary>
    public string Trigger { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether standard command generation is overridden.
    /// </summary>
    public bool UseCustomCommand { get; set; }

    /// <summary>
    /// Gets or sets the role list allowed to fire this transition.
    /// </summary>
    public List<string> RequiredRoles { get; set; } = new();

    /// <summary>
    /// Gets or sets the guard check rules list.
    /// </summary>
    public List<WorkflowConditionConfig> Conditions { get; set; } = new();

    /// <summary>
    /// Gets or sets the automated task execution list.
    /// </summary>
    public List<WorkflowActionConfig> Actions { get; set; } = new();
}

/// <summary>
/// Configuration for a transition guard expression.
/// </summary>
public class WorkflowConditionConfig
{
    /// <summary>
    /// Which object this guard reads: <c>entity</c> (the stored record) or <c>request</c> (the
    /// caller's command). Anything else — including absent — is treated as <c>entity</c>.
    /// </summary>
    /// <remarks>
    /// The engine used to evaluate every guard against the request and then the entity, passing if
    /// either satisfied it. That let a caller answer a guard about a value the server owns with a
    /// value they chose, and it decided choice-node routing too. Unrecognised values fall back to
    /// the entity rather than the request, so a typo cannot hand a guard to the caller.
    /// </remarks>
    public string Source { get; set; } = "entity";

    /// <summary>True when this guard reads the caller's command rather than the stored record.</summary>
    public bool ReadsRequest => string.Equals(Source, "request", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Gets or sets the type of comparison (e.g. PropertyComparison).
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the target request property name.
    /// </summary>
    public string Property { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the comparison operator name.
    /// </summary>
    public string Operator { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the comparison expected value.
    /// </summary>
    public string Value { get; set; } = string.Empty;
}

/// <summary>
/// Configuration for automated transition actions.
/// </summary>
public class WorkflowActionConfig
{
    /// <summary>
    /// Gets or sets the action type (e.g. InternalApi or ExternalApi).
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the internal MediatR command class name.
    /// </summary>
    public string? RequestType { get; set; }

    /// <summary>
    /// Gets or sets the JSON template for the MediatR command.
    /// </summary>
    public string? PayloadTemplate { get; set; }

    /// <summary>
    /// Gets or sets the external HTTP verb.
    /// </summary>
    public string? Method { get; set; }

    /// <summary>
    /// Gets or sets the target HTTP URL.
    /// </summary>
    public string? Url { get; set; }

    /// <summary>
    /// Gets or sets headers key-values map.
    /// </summary>
    public Dictionary<string, string>? Headers { get; set; }

    /// <summary>
    /// Gets or sets the HTTP request body template string.
    /// </summary>
    public string? BodyTemplate { get; set; }
}

/// <summary>
/// Configuration for a UML decision choice node gate.
/// </summary>
public class WorkflowChoiceNodeConfig
{
    /// <summary>
    /// Gets or sets the unique node ID.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the gate name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets target state when no branch condition matches.
    /// </summary>
    public string DefaultState { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the list of conditional branches leaving this node.
    /// </summary>
    public List<WorkflowChoiceBranchConfig> Branches { get; set; } = new();
}

/// <summary>
/// Configuration for a decision branch edge.
/// </summary>
public class WorkflowChoiceBranchConfig
{
    /// <summary>
    /// Gets or sets the target destination state.
    /// </summary>
    public string ToState { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets branch guard conditions list.
    /// </summary>
    public List<WorkflowConditionConfig> Conditions { get; set; } = new();
}
