using System;
using System.Collections.Generic;

namespace Foundry.Core.Security;

/// <summary>
/// Contract for owner-scoped entities whose rows can additionally be granted to other identities.
/// </summary>
/// <remarks>
/// <para>
/// Ownership answers "this row belongs to one caller" and nothing past it. Sharing, delegation, and
/// team or hierarchy scoping had nowhere to be expressed, so a schema needing any of them wrote the
/// rule by hand in a business rule — where nothing checked it was applied to every read path.
/// </para>
/// <para>
/// One mechanism covers all three, because they differ only in what the grant names. A row is
/// visible to its owner and to any identity in <see cref="SharedWith"/>; the caller's identities are
/// their subject plus the groups their token carries. Granting to a user id is sharing; granting to a
/// group id is team scoping; granting to a role-shaped group is delegation. Nothing in the data layer
/// needs to know which of those a deployment meant.
/// </para>
/// <para>
/// <strong>A grant confers read access only.</strong> Updates and deletes stay with the owner and
/// whoever holds an exempt role. That is the safe direction to be wrong in — a grant that silently
/// conferred write access would turn "let my colleague see this" into "let my colleague delete this"
/// — and it is stated here so that widening it later is a decision rather than a drift.
/// </para>
/// <para>
/// The property must be named <c>SharedWith</c>, for the same reason <c>OwnerId</c> and
/// <c>TenantId</c> must be named as they are: filters are built against the stored field by name, so
/// a differently-named property would produce a filter matching no documents. The compiler rejects
/// any other name rather than emitting code with that shape.
/// </para>
/// </remarks>
public interface ISharedResource : IOwnedResource
{
    /// <summary>
    /// Identities this row is granted to, in addition to its owner.
    /// </summary>
    /// <remarks>
    /// Each entry is either a subject id or a group id. Unlike <see cref="IOwnedResource.OwnerId"/>
    /// this is caller-settable — granting access is an ordinary operation on a row you own — so a
    /// caller can only change it through a write they were already permitted to make.
    /// </remarks>
    List<string> SharedWith { get; set; }
}

/// <summary>
/// Identifies the property holding the identities a row is granted to.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class SharedWithKeyAttribute : Attribute
{
}

/// <summary>
/// Roles that see every row in their tenant but may not change any of them.
/// </summary>
/// <remarks>
/// <para>
/// <c>OwnerExemptRoles</c> is per entity, not per operation: a role exempted from the owner filter
/// was exempted for reads, updates and deletes alike. Read-only oversight — the ordinary shape for an
/// auditor, a compliance reviewer, or a support agent who may look but not touch — could not be
/// expressed at all, and the closest approximation was combining an exemption with a role list on
/// DELETE, which does not cover updates and states the intent nowhere.
/// </para>
/// <para>
/// This lifts the owner filter on reads and leaves it in place on writes, so the holder sees the
/// whole tenant and can still only modify their own rows. Tenant isolation is unaffected, as with any
/// exemption: broader access within one tenant, never across tenants.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class OwnerReadExemptRolesAttribute : Attribute
{
    /// <summary>Roles exempt from the owner filter on reads only.</summary>
    public string[] Roles { get; }

    /// <summary>Initializes the attribute.</summary>
    public OwnerReadExemptRolesAttribute(params string[] roles)
    {
        Roles = roles ?? Array.Empty<string>();
    }
}

/// <summary>
/// The claim types a caller's group memberships are read from.
/// </summary>
/// <remarks>
/// Both spellings are accepted because identity providers disagree: Entra ID emits <c>groups</c>,
/// several OIDC servers emit <c>group</c>. Matching both costs nothing and avoids a deployment where
/// every grant silently fails to match because the provider chose the other spelling — which would
/// look exactly like "the user has no access", the least diagnosable outcome available.
/// </remarks>
public static class GroupClaims
{
    /// <summary>The claim types read as group memberships.</summary>
    public static readonly string[] Types = ["groups", "group"];
}
