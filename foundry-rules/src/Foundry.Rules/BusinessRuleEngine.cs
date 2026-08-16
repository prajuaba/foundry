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
/// <remarks>
/// This engine must be registered as <c>scoped</c>, not singleton. It injects <see cref="IServiceProvider"/>
/// and resolves <see cref="IBusinessRule{TRequest}"/> instances from it using
/// <c>GetServices&lt;IBusinessRule&lt;TRequest&gt;&gt;()</c>. Rules legitimately depend on scoped services
/// such as repositories (<see cref="Foundry.Mongo.Repositories.IRepository{T}"/>) and
/// <see cref="Foundry.Core.User.ICurrentUserContext"/>. If the engine were a singleton, it would receive
/// the root service provider at registration time, and attempting to resolve rules with scoped dependencies
/// would fail with <c>InvalidOperationException: Cannot resolve ... from root provider because it requires
/// scoped service</c>.
///
/// By registering the engine as scoped, each request receives a fresh engine instance that is resolved
/// from the request's scoped provider, so when the engine resolves rules, they can access the request's
/// scoped dependencies. This is safe because the engine's only consumer is
/// <see cref="Foundry.Api.MediatR.Behaviors.BusinessRuleBehavior{TRequest,TResponse}"/>, which is itself
/// transient or scoped.
///
/// Do not attempt to "fix" this by injecting <c>IServiceScopeFactory</c> and creating a new scope inside
/// <c>EvaluateAsync</c>. A fresh scope has a different <see cref="Foundry.Core.User.ICurrentUserContext"/>,
/// so rules would evaluate against the wrong caller or none at all. Rules must see the request's own scope.
/// </remarks>
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
