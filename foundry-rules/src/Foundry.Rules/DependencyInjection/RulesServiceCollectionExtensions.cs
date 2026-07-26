using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Foundry.Rules;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Service collection extensions for registering the business rules engine.
/// </summary>
public static class RulesServiceCollectionExtensions
{
    /// <summary>
    /// Registers the business rules engine core services.
    /// </summary>
    public static IServiceCollection AddFoundryRules(this IServiceCollection services)
    {
        services.TryAddSingleton<IBusinessRuleEngine, BusinessRuleEngine>();
        services.TryAddSingleton<IWorkflowEngine, WorkflowEngine>();
        services.TryAddSingleton<IDynamicRuleStore, InMemoryDynamicRuleStore>();
        services.AddTransient(typeof(IBusinessRule<>), typeof(DynamicRulesEngineRule<>));

        // Register resilient HTTP client pipeline with Polly v8 standard resilience (Retries, Circuit Breaker, Timeout, Jitter)
        services.AddHttpClient("FoundryWorkflow")
            .AddStandardResilienceHandler();

        return services;
    }
}
