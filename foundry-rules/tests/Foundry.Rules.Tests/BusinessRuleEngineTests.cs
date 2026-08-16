using Foundry.Rules;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Foundry.Rules.Tests;

/// <summary>
/// Rule aggregation and enforcement in <see cref="BusinessRuleEngine"/>.
/// </summary>
public class BusinessRuleEngineTests
{
    private sealed record OrderRequest(decimal Total);

    private sealed class PassingRule : IBusinessRule<OrderRequest>
    {
        public Task<RuleResult> ValidateAsync(OrderRequest request, CancellationToken ct = default)
            => Task.FromResult(RuleResult.Success());
    }

    private sealed class FailingRule(string message) : IBusinessRule<OrderRequest>
    {
        public Task<RuleResult> ValidateAsync(OrderRequest request, CancellationToken ct = default)
            => Task.FromResult(RuleResult.Failure(message));
    }

    private sealed class ThrowingRule : IBusinessRule<OrderRequest>
    {
        public Task<RuleResult> ValidateAsync(OrderRequest request, CancellationToken ct = default)
            => throw new InvalidOperationException("rule blew up");
    }

    private static BusinessRuleEngine EngineWith(params IBusinessRule<OrderRequest>[] rules)
    {
        var services = new ServiceCollection();
        foreach (var rule in rules) services.AddSingleton(rule);
        return new BusinessRuleEngine(services.BuildServiceProvider());
    }

    [Fact]
    public async Task NoRulesRegistered_PassesWithNoFailures()
    {
        var failures = await EngineWith().EvaluateAsync(new OrderRequest(10m), default);
        Assert.Empty(failures);
    }

    [Fact]
    public async Task OnlyFailuresAreReturned()
    {
        var engine = EngineWith(new PassingRule(), new FailingRule("too small"), new PassingRule());

        var failures = (await engine.EvaluateAsync(new OrderRequest(10m), default)).ToList();

        Assert.Single(failures);
        Assert.Equal("too small", failures[0].ErrorMessage);
    }

    [Fact]
    public async Task EveryRuleRuns_SoAllFailuresAreReportedTogether()
    {
        // Stopping at the first failure would make a caller fix one problem, resubmit, and be told
        // about the next -- so all failures are collected in a single pass.
        var engine = EngineWith(new FailingRule("first"), new FailingRule("second"));

        var failures = (await engine.EvaluateAsync(new OrderRequest(10m), default)).ToList();

        Assert.Equal(2, failures.Count);
        Assert.Contains(failures, f => f.ErrorMessage == "first");
        Assert.Contains(failures, f => f.ErrorMessage == "second");
    }

    [Fact]
    public async Task EnsurePassed_IsSilentWhenAllRulesPass()
    {
        await EngineWith(new PassingRule()).EnsurePassedAsync(new OrderRequest(10m), default);
    }

    [Fact]
    public async Task EnsurePassed_ThrowsCarryingEveryFailure()
    {
        var engine = EngineWith(new FailingRule("first"), new FailingRule("second"));

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => engine.EnsurePassedAsync(new OrderRequest(10m), default));

        // The failures must reach the caller: an exception that says "validation failed" without
        // saying what failed forces a log dive for what is really a 400-level response body.
        Assert.Equal(2, error.Failures.Count);
    }

    [Fact]
    public async Task RuleThatThrows_PropagatesRatherThanCountingAsAPass()
    {
        // A rule that throws must not be swallowed into "no failures found", which would let the
        // request through precisely because its validation was broken.
        var engine = EngineWith(new ThrowingRule());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => engine.EnsurePassedAsync(new OrderRequest(10m), default));
    }

    [Fact]
    public async Task RulesForOtherRequestTypes_AreNotApplied()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IBusinessRule<OrderRequest>>(new FailingRule("order rule"));
        var engine = new BusinessRuleEngine(services.BuildServiceProvider());

        // A rule registered for OrderRequest must not fire for an unrelated request type.
        var failures = await engine.EvaluateAsync("some other request", default);

        Assert.Empty(failures);
    }

    // === Tests for scoped engine fix ===

    [Fact]
    public void Engine_IsRegisteredAsScoped_NotSingleton()
    {
        // The engine must be scoped so it receives the request's scoped provider, allowing rules
        // to resolve scoped dependencies like repositories and ICurrentUserContext.
        var services = new ServiceCollection();
        services.AddFoundryRules();

        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IBusinessRuleEngine));
        Assert.NotNull(descriptor);
        Assert.Equal(ServiceLifetime.Scoped, descriptor!.Lifetime);
    }

    [Fact]
    public async Task RuleWithScopedDependency_ResolvesAndExecutesFromRequestScope()
    {
        // Scoped dependency interface
        var interface_scoped = typeof(IScopedDependency);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFoundryRules();

        // Register the scoped dependency and the rule that uses it
        services.AddScoped<IScopedDependency, ScopedDependency>();
        services.AddScoped<IBusinessRule<OrderRequest>, RuleWithScopedDependency>();

        // Build with ValidateScopes to catch scoped-in-singleton violations
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });

        // Create a scope and resolve the engine from within it (like a request would)
        using var scope = provider.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<IBusinessRuleEngine>();

        // This should not throw -- the engine should resolve the rule and the rule should resolve
        // its scoped dependency from the same scope
        var failures = (await engine.EvaluateAsync(new OrderRequest(10m), default)).ToList();

        Assert.Empty(failures);
    }

    [Fact]
    public async Task RuleSeesTheSameScopedInstance_AsTheSurroundingScope()
    {
        // This test proves that rules see the request's own scoped context, not a fresh one.
        // If we (wrongly) created a new scope inside EvaluateAsync, this test would fail.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFoundryRules();

        // Register the scoped dependency and the rule
        services.AddScoped<IScopedDependency, ScopedDependency>();
        services.AddScoped<IBusinessRule<OrderRequest>, RuleCapturingScopedDependency>();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true
        });

        using var scope = provider.CreateScope();

        // Get the engine and the scoped dependency from the same scope
        var engine = scope.ServiceProvider.GetRequiredService<IBusinessRuleEngine>();
        var directDependency = scope.ServiceProvider.GetRequiredService<IScopedDependency>();

        // Evaluate a rule that captures the dependency's identity
        var failures = (await engine.EvaluateAsync(new OrderRequest(10m), default)).ToList();

        // The rule should have captured the dependency that came from this scope
        Assert.Empty(failures);
        Assert.True(RuleCapturingScopedDependency.LastCapturedId == directDependency.Id,
            $"Rule captured dependency Id {RuleCapturingScopedDependency.LastCapturedId} but the scope's dependency has Id {directDependency.Id}");
    }

    [Fact]
    public async Task TwoDifferentScopes_GiveRulesTwoDifferentScopedInstances()
    {
        // This test ensures that scoped dependencies are truly scoped to the request, not shared
        // across requests.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFoundryRules();

        services.AddScoped<IScopedDependency, ScopedDependency>();
        services.AddScoped<IBusinessRule<OrderRequest>, RuleCapturingScopedDependency>();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true
        });

        // First scope
        using var scope1 = provider.CreateScope();
        RuleCapturingScopedDependency.LastCapturedId = -1; // Reset
        var engine1 = scope1.ServiceProvider.GetRequiredService<IBusinessRuleEngine>();
        await engine1.EvaluateAsync(new OrderRequest(10m), default);
        var id1 = RuleCapturingScopedDependency.LastCapturedId;

        // Second scope
        using var scope2 = provider.CreateScope();
        RuleCapturingScopedDependency.LastCapturedId = -1; // Reset
        var engine2 = scope2.ServiceProvider.GetRequiredService<IBusinessRuleEngine>();
        await engine2.EvaluateAsync(new OrderRequest(10m), default);
        var id2 = RuleCapturingScopedDependency.LastCapturedId;

        // The two scopes should have provided different instances of the scoped dependency
        Assert.NotEqual(id1, id2);
    }

    // === Scoped dependency and rules for testing ===

    private interface IScopedDependency
    {
        int Id { get; }
    }

    private sealed class ScopedDependency : IScopedDependency
    {
        private static int _nextId = 1;
        public int Id { get; }

        public ScopedDependency()
        {
            Id = _nextId++;
        }
    }

    private sealed class RuleWithScopedDependency : IBusinessRule<OrderRequest>
    {
        private readonly IScopedDependency _dependency;

        public RuleWithScopedDependency(IScopedDependency dependency)
        {
            _dependency = dependency;
        }

        public Task<RuleResult> ValidateAsync(OrderRequest request, CancellationToken ct = default)
        {
            // The fact that this rule was instantiated with a scoped dependency means
            // the engine was able to resolve it from the scoped provider.
            return Task.FromResult(RuleResult.Success());
        }
    }

    private sealed class RuleCapturingScopedDependency : IBusinessRule<OrderRequest>
    {
        private readonly IScopedDependency _dependency;

        // Static field to capture the dependency Id for assertion
        public static int LastCapturedId { get; set; }

        public RuleCapturingScopedDependency(IScopedDependency dependency)
        {
            _dependency = dependency;
        }

        public Task<RuleResult> ValidateAsync(OrderRequest request, CancellationToken ct = default)
        {
            LastCapturedId = _dependency.Id;
            return Task.FromResult(RuleResult.Success());
        }
    }
}
