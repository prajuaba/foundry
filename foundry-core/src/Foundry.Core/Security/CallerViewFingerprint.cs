using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace Foundry.Core.Security;

/// <summary>
/// A stable digest of everything about a caller that changes what a query returns them.
/// </summary>
/// <remarks>
/// <para>
/// Foundry narrows and redacts results in the data layer: the tenant filter, the owner filter,
/// grant widening, role exemptions and per-category masking are all applied inside the repository.
/// Any cache that sits above that — and there are two, the API pipeline's
/// <c>CachingBehavior</c> and the data layer's <c>CachedRepository</c> — therefore holds a value
/// that is correct only for the caller who produced it. Keying such a cache on the query alone
/// lets the first caller to run it decide what every later caller sees.
/// </para>
/// <para>
/// Both caches had that defect and it was reproducible with two tokens and one endpoint. Tenant
/// <c>acme</c> listed its two projects and tenant <c>globex</c> was served those same two rows; a
/// caller holding no unmasking scope populated an entry and a caller holding <c>view:contact</c>
/// then read the masked value out of it. This type exists so the two caches cannot drift on what
/// "the same caller" means, because a disagreement between them is a disclosure.
/// </para>
/// <para>
/// What goes in is exactly what the data layer reads, and no more:
/// </para>
/// <list type="bullet">
/// <item><description><b>tenant</b> — the discriminator the repository filters on.</description></item>
/// <item><description><b>subject</b> — what owner-scoped entities filter rows by.</description></item>
/// <item><description><b>groups</b> — what widens an owner's rows to a shared set.</description></item>
/// <item><description><b>roles</b> — because owner-exempt and read-exempt roles see rows their
/// holder does not own.</description></item>
/// <item><description><b>unmasking scopes</b> — the one input that changes the contents of a row
/// rather than which rows come back.</description></item>
/// </list>
/// <para>
/// The cost is a lower hit rate: two callers share an entry only when all five agree, which on an
/// owner-scoped entity means per-user caching. That is the correct price, because an entry shared
/// more widely than the data it holds is not a faster cache.
/// </para>
/// <para>
/// The result is hashed rather than readable. Cache keys are logged on every hit and miss, and
/// subjects and group names are not log material.
/// </para>
/// </remarks>
public static class CallerViewFingerprint
{
    /// <summary>The fingerprint used when there is no tenant and no readable principal.</summary>
    /// <remarks>
    /// Shared only with other callers who present nothing at all. Failing closed would mean never
    /// caching; failing open would mean the defect this replaces.
    /// </remarks>
    public const string Anonymous = "anonymous";

    /// <summary>
    /// Computes the fingerprint for one caller.
    /// </summary>
    public static string Compute(string? tenantId, ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true && string.IsNullOrEmpty(tenantId))
        {
            return Anonymous;
        }

        var parts = new List<string> { "t=" + (tenantId ?? string.Empty) };

        if (principal is not null)
        {
            parts.Add("s=" + (principal.FindFirst("sub")?.Value
                              ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
                              ?? string.Empty));

            parts.Add("g=" + Joined(principal, c => Array.IndexOf(GroupClaims.Types, c.Type) >= 0));

            // Both spellings, for the same reason EntityAccessPolicy reads both: MapInboundClaims
            // is off and the role claim type is configurable.
            parts.Add("r=" + Joined(principal, c => c.Type == "role" || c.Type == ClaimTypes.Role));

            // Only the view: scopes. An OAuth 'scope' claim carries plenty that has no bearing on
            // what a row looks like, and folding all of it in would fragment the cache for nothing.
            parts.Add("v=" + Joined(principal, c =>
                c.Type == ViewSensitiveDataScope.ClaimType
                && c.Value.StartsWith("view:", StringComparison.Ordinal)));
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", parts))));
    }

    /// <summary>
    /// Claim values matching <paramref name="match"/>, deduplicated and ordered so that a principal
    /// presenting the same claims in a different order produces the same fingerprint.
    /// </summary>
    private static string Joined(ClaimsPrincipal principal, Func<Claim, bool> match)
        => string.Join(",", principal.Claims
            .Where(match)
            .Select(c => c.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(v => v, StringComparer.Ordinal));
}
