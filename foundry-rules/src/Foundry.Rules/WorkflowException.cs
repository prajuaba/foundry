using System;

namespace Foundry.Rules;

/// <summary>
/// Exception thrown when a workflow state transition or security check policy fails validation.
/// </summary>
public class WorkflowException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="WorkflowException"/> class.
    /// </summary>
    /// <param name="message">The exception error message details.</param>
    public WorkflowException(string message) : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="WorkflowException"/> class.
    /// </summary>
    /// <param name="message">The exception error message details.</param>
    /// <param name="innerException">The inner exception reason reference.</param>
    public WorkflowException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
