using System;

namespace Foundry.Core.Security;

/// <summary>
/// Contract for entities whose rows belong to an individual caller rather than to the whole tenant.
/// </summary>
/// <remarks>
/// <para>
/// Tenancy answers "which organisation's data is this"; ownership answers "which person's". They
/// compose: an owner filter never replaces the tenant filter, it narrows within it. A caller sees
/// rows that are in their tenant <em>and</em> theirs, unless they hold an exempt role.
/// </para>
/// <para>
/// The property must be named <c>OwnerId</c>. That is a constraint of the data layer rather than a
/// style preference: filters are built against the stored field by name, so a differently-named
/// property would produce a filter matching no documents — a read returning nothing, or worse a
/// write scoped by a predicate that never matches. The compiler rejects any other name (FDY3004)
/// rather than emitting code with that shape.
/// </para>
/// </remarks>
public interface IOwnedResource
{
    /// <summary>
    /// Identifier of the caller this row belongs to, taken from their authenticated identity.
    /// </summary>
    /// <remarks>
    /// Server-assigned on write and never read from the request body: a caller that could set this
    /// could create rows owned by somebody else, or reassign one of their own.
    /// </remarks>
    string OwnerId { get; set; }
}

/// <summary>
/// Identifies the property that records which caller owns a row.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class OwnerKeyAttribute : Attribute
{
}

/// <summary>
/// Roles that see every row in their tenant, not only their own.
/// </summary>
/// <remarks>
/// <para>
/// Applied to the entity, because it is a policy about the entity rather than about any one row.
/// Supervisors, auditors and support staff need this; it is the reason ownership can be enforced by
/// default without making the feature unusable.
/// </para>
/// <para>
/// Exemption lifts the <em>owner</em> filter only. Tenant isolation still applies, so an exempt role
/// is broader access within one tenant and never across tenants.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class OwnerExemptRolesAttribute : Attribute
{
    /// <summary>Roles exempt from the owner filter for this entity.</summary>
    public string[] Roles { get; }

    public OwnerExemptRolesAttribute(params string[] roles)
    {
        Roles = roles ?? Array.Empty<string>();
    }
}
