using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Foundry.Rules;

/// <summary>
/// Defines operations to evaluate, validate, and execute state transitions.
/// </summary>
public interface IWorkflowEngine
{
    /// <summary>
    /// Validates if the user's role list matches the transition requirement or current state matrix.
    /// </summary>
    void ValidatePermission(
        string transitionId,
        string currentState,
        List<string> transitionRoles,
        List<string> stateRoles,
        IEnumerable<string> userRoles);

    /// <summary>
    /// Evaluates guard condition expressions against request parameter values.
    /// </summary>
    bool EvaluateCondition(string propertyName, string op, string expectedValue, object requestPayload);

    /// <summary>
    /// Executes transition actions (Internal MediatR sends or External HTTP REST API calls).
    /// </summary>
    Task<ActionExecutionDetail> ExecuteActionAsync(
        string actionType,
        string? requestType,
        string? payloadTemplate,
        string? method,
        string? url,
        Dictionary<string, string>? headers,
        string? bodyTemplate,
        object requestPayload,
        CancellationToken ct);
}
