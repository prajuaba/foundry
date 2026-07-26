using System.Threading;
using System.Threading.Tasks;

namespace Foundry.Rules;

/// <summary>
/// Contract for custom asynchronous business rule validations executed within command pipelines.
/// </summary>
public interface IBusinessRule<in TRequest>
{
    /// <summary>
    /// Evaluates the business rule against the incoming request context.
    /// </summary>
    Task<RuleResult> ValidateAsync(TRequest request, CancellationToken ct);
}
