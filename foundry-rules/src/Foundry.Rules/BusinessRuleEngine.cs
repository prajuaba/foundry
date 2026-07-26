using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace Foundry.Rules;

/// <summary>
/// Default implementation of the business rule engine.
/// </summary>
public class BusinessRuleEngine : IBusinessRuleEngine
{
    private readonly IServiceProvider _serviceProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="BusinessRuleEngine"/> class.
    /// </summary>
    /// <param name="serviceProvider">The application's service provider to resolve rules.</param>
    public BusinessRuleEngine(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    /// <inheritdoc />
    public async Task<IEnumerable<RuleResult>> EvaluateAsync<TRequest>(TRequest request, CancellationToken ct)
    {
        var rules = _serviceProvider.GetServices<IBusinessRule<TRequest>>();
        var results = new List<RuleResult>();

        foreach (var rule in rules)
        {
            var result = await rule.ValidateAsync(request, ct);
            if (!result.IsPassed)
            {
                results.Add(result);
            }
        }

        return results;
    }

    /// <inheritdoc />
    public async Task EnsurePassedAsync<TRequest>(TRequest request, CancellationToken ct)
    {
        var failures = (await EvaluateAsync(request, ct)).ToList();
        if (failures.Any())
        {
            throw new BusinessRuleException("One or more business rule policy validations failed.", failures);
        }
    }
}
