namespace Foundry.Rules;

/// <summary>
/// Defines status properties needed for entities participating in the Foundry Workflow System.
/// </summary>
public interface IWorkflowStateful
{
    /// <summary>
    /// Gets or sets the current workflow state.
    /// </summary>
    string CurrentState { get; set; }

    /// <summary>
    /// Gets or sets the workflow definition ID controlling this entity.
    /// </summary>
    string WorkflowId { get; set; }

    /// <summary>
    /// Gets or sets the version string of the workflow definition in use.
    /// </summary>
    string WorkflowVersion { get; set; }
}
