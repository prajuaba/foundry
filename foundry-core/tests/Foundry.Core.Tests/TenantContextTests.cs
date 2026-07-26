using Foundry.Core.Tenant;
using Xunit;

namespace Foundry.Core.Tests;

/// <summary>
/// Ambient tenant propagation in <see cref="TenantContext"/>.
/// </summary>
/// <remarks>
/// This is the root of tenant isolation: the MongoDB repository reads the ambient tenant id and
/// injects it as a filter on every query. If the value leaks between concurrent requests, one
/// customer reads another customer's data, and nothing in the system reports an error — the query
/// succeeds and returns the wrong rows. The concurrency tests below are the point of this file; the
/// rest is scaffolding around them.
/// </remarks>
[Collection("TenantContext")]
public class TenantContextTests
{
    [Fact]
    public void SetThenRead_ReturnsTheTenant()
    {
        var context = new TenantContext();
        context.SetTenantId("acme");

        Assert.Equal("acme", context.TenantId);
        Assert.True(context.HasTenant);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void SettingABlankTenant_IsRejected(string blank)
    {
        // Accepting a blank tenant would be the dangerous failure: the repository would inject an
        // empty filter and the query would span every tenant.
        Assert.Throws<ArgumentException>(() => new TenantContext().SetTenantId(blank));
    }

    [Fact]
    public void SettingANullTenant_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => new TenantContext().SetTenantId(null!));
    }

    [Fact]
    public void ARejectedSet_DoesNotDisturbTheCurrentTenant()
    {
        var context = new TenantContext();
        context.SetTenantId("acme");

        Assert.Throws<ArgumentException>(() => context.SetTenantId(""));

        Assert.Equal("acme", context.TenantId);
    }

    [Fact]
    public async Task ConcurrentFlows_DoNotSeeEachOthersTenant()
    {
        // The isolation property. Each task sets its own tenant after the async fork, so each gets
        // its own copy of the ambient value. If this ever fails, requests are cross-contaminating.
        var observed = new string?[50];

        await Task.WhenAll(Enumerable.Range(0, observed.Length).Select(index => Task.Run(async () =>
        {
            var context = new TenantContext();
            context.SetTenantId($"tenant-{index}");

            // Yield repeatedly so the tasks genuinely interleave rather than running to completion
            // one at a time, which would pass regardless of whether the value is isolated.
            for (var i = 0; i < 5; i++) await Task.Yield();

            observed[index] = context.TenantId;
        })));

        for (var index = 0; index < observed.Length; index++)
        {
            Assert.Equal($"tenant-{index}", observed[index]);
        }
    }

    [Fact]
    public async Task ATenantSetInsideATask_DoesNotEscapeToTheCaller()
    {
        // Otherwise a background operation for tenant B would silently retarget the request that
        // spawned it.
        var context = new TenantContext();
        context.SetTenantId("outer");

        await Task.Run(() => new TenantContext().SetTenantId("inner"));

        Assert.Equal("outer", context.TenantId);
    }

    [Fact]
    public async Task ChildOperations_InheritTheCallersTenant()
    {
        // The flip side, and the behaviour the repository depends on: work started by a request
        // must stay scoped to that request's tenant.
        var context = new TenantContext();
        context.SetTenantId("acme");

        var seen = await Task.Run(async () =>
        {
            await Task.Yield();
            return new TenantContext().TenantId;
        });

        Assert.Equal("acme", seen);
    }

    [Fact]
    public void AllInstances_ShareTheAmbientValue()
    {
        // TenantContext is ambient by design, so the DI lifetime it is registered with must not
        // change behaviour. Asserted so nobody "fixes" it into per-instance state, which would make
        // the filter depend on which instance the repository happened to receive.
        var writer = new TenantContext();
        var reader = new TenantContext();

        writer.SetTenantId("acme");

        Assert.Equal("acme", reader.TenantId);
    }

    [Fact]
    public void WithNoTenantSet_HasTenantIsFalse()
    {
        // Runs in its own flow so a value set by another test cannot make this pass or fail.
        var task = Task.Run(() =>
        {
            var context = new TenantContext();
            return (context.HasTenant, context.TenantId);
        });

        var (hasTenant, tenantId) = task.GetAwaiter().GetResult();

        Assert.False(hasTenant);
        Assert.Null(tenantId);
    }
}
