using System.Security.Claims;
using Foundry.Core.Attributes;
using Foundry.RealTime;
using Xunit;

namespace Foundry.RealTime.Tests;

/// <summary>
/// Roles were the only question the policy asked, so a subscriber authenticated to one tenant
/// received mutation events for every other one: entity type, entity id, collection name, operator
/// and timestamp. Observed on a running application — a subscriber on tenant "globex" was handed the
/// insert of a Project created in "acme", with the same entity id.
/// </summary>
public class RealTimeTenantIsolationTests
{
    [RealTime(true, new[] { "Admin" })]
    public sealed record TenantedThing;

    private static ClaimsPrincipal Caller(string? tenant, params string[] roles)
    {
        var claims = new List<Claim> { new(ClaimTypes.Name, "ada") };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        if (tenant is not null) claims.Add(new Claim("tenant_id", tenant));

        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "test"));
    }

    private static string EntityName => typeof(TenantedThing).FullName!;

    [Fact]
    public void ACallerSeesTheirOwnTenantsEvents()
    {
        Assert.True(RealTimeAccessPolicy.MayObserve(
            Caller("acme", "Admin"), EntityName, "acme", out _));
    }

    [Fact]
    public void ACallerDoesNotSeeAnotherTenantsEvents()
    {
        // The defect, exactly: same entity, same role, different tenant.
        var allowed = RealTimeAccessPolicy.MayObserve(
            Caller("globex", "Admin"), EntityName, "acme", out var reason);

        Assert.False(allowed);
        Assert.NotNull(reason);
        // The other tenant's identifier must not appear in a message about refusing access to it.
        Assert.DoesNotContain("acme", reason!);
    }

    [Fact]
    public void AnUntenantedCallerDoesNotSeeATenantedEvent()
    {
        Assert.False(RealTimeAccessPolicy.MayObserve(
            Caller(null, "Admin"), EntityName, "acme", out _));
    }

    [Fact]
    public void ATenantedCallerDoesNotSeeAnUnattributableEvent()
    {
        // Entries written before audit entries carried a tenant have none. An event that cannot say
        // whose it is must not go to whoever asks first.
        Assert.False(RealTimeAccessPolicy.MayObserve(
            Caller("acme", "Admin"), EntityName, null, out _));
    }

    [Fact]
    public void ASingleTenantDeploymentStillWorks()
    {
        // Neither side carries a tenant, which is what a single-tenant application looks like.
        // A rule that simply demanded a tenant would silently switch real-time off for them.
        Assert.True(RealTimeAccessPolicy.MayObserve(
            Caller(null, "Admin"), EntityName, null, out _));
    }

    [Fact]
    public void TheRoleIsStillCheckedFirst()
    {
        // Matching tenants do not excuse a missing role.
        Assert.False(RealTimeAccessPolicy.MayObserve(
            Caller("acme", "Warehouse"), EntityName, "acme", out _));
    }
}
