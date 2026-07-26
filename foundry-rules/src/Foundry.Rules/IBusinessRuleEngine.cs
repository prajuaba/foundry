using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Foundry.Rules;

/// <summary>
/// Service coordinator that aggregates and evaluates registered business rules for a given request.
/// </summary>
public interface IBusinessRuleEngine
{
    /// <summary>
    /// Evaluates all registered business rules for the request and returns the list of failures.
    /// </summary>
    Task<IEnumerable<RuleResult>> EvaluateAsync<TRequest>(TRequest request, CancellationToken ct);

    /// <summary>
    /// Evaluates all registered business rules and throws BusinessRuleException if any rule fails.
    /// </summary>
    Task EnsurePassedAsync<TRequest>(TRequest request, CancellationToken ct);
}
