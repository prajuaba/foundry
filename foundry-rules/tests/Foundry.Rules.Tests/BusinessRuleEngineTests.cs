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
}
