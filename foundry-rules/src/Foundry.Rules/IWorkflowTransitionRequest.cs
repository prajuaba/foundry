using MediatR;

namespace Foundry.Rules;

/// <summary>
/// Defines properties for a request command that initiates a workflow state machine transition.
/// </summary>
public interface IWorkflowTransitionRequest
{
    /// <summary>
    /// Gets the unique string ID of the target entity document.
    /// </summary>
    string EntityId { get; }

    /// <summary>
    /// Gets the entity type name.
    /// </summary>
    string EntityType { get; }

    /// <summary>
    /// Gets the unique identifier of the transition.
    /// </summary>
    string TransitionId { get; }

    /// <summary>
    /// Gets the state transitioned from.
    /// </summary>
    string FromState { get; }

    /// <summary>
    /// Gets the state transitioned to.
    /// </summary>
    string ToState { get; }
}
