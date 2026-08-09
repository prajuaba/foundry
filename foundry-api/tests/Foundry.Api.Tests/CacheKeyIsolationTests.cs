using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using MediatR;
using NSubstitute;
using Xunit;
using Foundry.Api.MediatR.Behaviors;
using Foundry.Core.Tenant;

namespace Foundry.Api.Tests;

/// <summary>
/// The cache key must separate callers whose view of the same query differs.
/// </summary>
/// <remarks>
/// <para>
/// The key used to be the request type and its properties, and nothing else. Everything that
/// narrows or redacts a result — the tenant filter, the owner filter, grant widening, role
/// exemptions, per-category masking — is applied in the repository, underneath the caching
/// behavior, so that key described a query whose answer depends on who asked. The first caller to
/// run it populated an entry every later caller read.
/// </para>
/// <para>
/// Reproduced end to end before the fix, against a schema whose every entity is multi-tenant:
/// tenant <c>acme</c> listed its two projects, tenant <c>globex</c> was served those same two rows,
/// and a project <c>globex</c> had itself just written was missing from them.
/// </para>
/// <para>
/// These drive <c>BuildCacheKey</c> directly. Going through the pipeline would test MediatR and the
/// endpoint metadata as much as the key, and the property under test is simply that two principals
/// who may see different things never produce one string.
/// </para>
/// </remarks>
public class CacheKeyIsolationTests
{
    private sealed record ProbeQuery(string Filter) : IRequest<string>;

    private static string KeyFor(
        string? tenant = null,
        string? subject = null,
        IEnumerable<string>? roles = null,
        IEnumerable<string>? groups = null,
        IEnumerable<string>? scopes = null,
        string filter = "same-query")
    {
        var claims = new List<Claim>();
        if (subject is not null) claims.Add(new Claim("sub", subject));
        foreach (var r in roles ?? []) claims.Add(new Claim("role", r));
        foreach (var g in groups ?? []) claims.Add(new Claim("groups", g));
        foreach (var s in scopes ?? []) claims.Add(new Claim("scope", s));

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test"));

        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(new DefaultHttpContext { User = principal });

        var tenantContext = Substitute.For<ITenantContext>();
        tenantContext.TenantId.Returns(tenant);
        tenantContext.HasTenant.Returns(tenant is not null);

        var behavior = new CachingBehavior<ProbeQuery, string>(
            new MemoryCache(new MemoryCacheOptions()),
            accessor,
            NullLogger<CachingBehavior<ProbeQuery, string>>.Instance,
            distributedCache: null,
            tenantContext: tenantContext);

        var method = typeof(CachingBehavior<ProbeQuery, string>)
            .GetMethod("BuildCacheKey", BindingFlags.NonPublic | BindingFlags.Instance)!;

        return (string)method.Invoke(behavior, [typeof(ProbeQuery), new ProbeQuery(filter)])!;
    }

    [Fact]
    public void TwoTenantsRunningTheSameQueryDoNotShareAnEntry()
    {
        // The defect, at its smallest.
        Assert.NotEqual(
            KeyFor(tenant: "acme", subject: "u1"),
            KeyFor(tenant: "globex", subject: "u1"));
    }

    [Fact]
    public void TwoSubjectsInOneTenantDoNotShareAnEntry()
    {
        // Owner-scoped entities filter rows by the caller's subject, below this cache.
        Assert.NotEqual(
            KeyFor(tenant: "acme", subject: "member-one"),
            KeyFor(tenant: "acme", subject: "member-two"));
    }

    [Fact]
    public void ARoleThatExemptsTheOwnerFilterDoesNotShareAnEntry()
    {
        // ownerExemptRoles and ownerReadExemptRoles see rows their holder does not own.
        Assert.NotEqual(
            KeyFor(tenant: "acme", subject: "u1", roles: ["TeamMember"]),
            KeyFor(tenant: "acme", subject: "u1", roles: ["TeamMember", "Auditor"]));
    }

    [Fact]
    public void AGroupMembershipThatWidensGrantsDoesNotShareAnEntry()
    {
        // SharedWith widens an owner's rows to anyone named in the set, groups included.
        Assert.NotEqual(
            KeyFor(tenant: "acme", subject: "u1", groups: ["engineering"]),
            KeyFor(tenant: "acme", subject: "u1", groups: ["engineering", "finance"]));
    }

    [Fact]
    public void AnUnmaskingScopeDoesNotShareAnEntry()
    {
        // The one that changes the contents of a row rather than which rows come back: a response
        // cached for a holder of view:pii must not be replayed to someone without it.
        Assert.NotEqual(
            KeyFor(tenant: "acme", subject: "u1"),
            KeyFor(tenant: "acme", subject: "u1", scopes: ["view:pii"]));
    }

    [Fact]
    public void OneCallerRunningOneQueryTwiceDoesShareAnEntry()
    {
        // Keying on the caller is only correct if it still caches. Without this the suite would
        // pass for a key that included a timestamp.
        Assert.Equal(
            KeyFor(tenant: "acme", subject: "u1", roles: ["Admin"], scopes: ["view:pii"]),
            KeyFor(tenant: "acme", subject: "u1", roles: ["Admin"], scopes: ["view:pii"]));
    }

    [Fact]
    public void ClaimOrderDoesNotChangeTheKey()
    {
        // A principal presenting the same claims in a different order is the same caller.
        Assert.Equal(
            KeyFor(tenant: "acme", subject: "u1", roles: ["Admin", "PMO"], groups: ["a", "b"]),
            KeyFor(tenant: "acme", subject: "u1", roles: ["PMO", "Admin"], groups: ["b", "a"]));
    }

    [Fact]
    public void DifferentQueriesStillDiffer()
    {
        Assert.NotEqual(
            KeyFor(tenant: "acme", subject: "u1", filter: "one"),
            KeyFor(tenant: "acme", subject: "u1", filter: "two"));
    }

    [Fact]
    public void TheKeyDoesNotCarryTheSubjectInClearText()
    {
        // Keys are logged at Information on every hit and miss. Subjects and group names are not
        // log material, so the caller half is hashed.
        var key = KeyFor(tenant: "acme", subject: "alice@example.com", groups: ["payroll-admins"]);

        Assert.DoesNotContain("alice@example.com", key, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("payroll-admins", key, StringComparison.OrdinalIgnoreCase);
    }
}
