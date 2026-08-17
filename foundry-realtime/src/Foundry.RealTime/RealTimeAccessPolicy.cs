using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using Foundry.Core.Attributes;

namespace Foundry.RealTime;

/// <summary>
/// Decides who may observe an entity's real-time events. The single implementation of that rule.
/// </summary>
/// <remarks>
/// <para>
/// <c>[RealTime(enabled, roles)]</c> comes from the schema's <c>enableRealTime</c> and
/// <c>realTimeRoles</c>, and this framework ships three transports for the events it governs:
/// SignalR, Server-Sent Events and raw WebSockets. The rule was implemented in one of them.
/// </para>
/// <para>
/// <c>NotificationHub</c> checked the roles when a client subscribed, and SignalR delivery was
/// narrowed to subscription groups precisely because an unconditional <c>Clients.All</c> send
/// bypassed that check. SSE and WebSockets <em>were</em> that send: neither had any notion of a
/// subscription, neither recorded who was connected, and both handed every mutation in the system to
/// every connected client. An <c>AuditLogEntry</c> carries <c>PropertyDiffs</c> — the changed values
/// — so any authenticated caller could watch the contents of every write to every entity, whatever
/// roles the schema said were required, by asking for the other two URLs.
/// </para>
/// <para>
/// So the decision lives here and every transport calls it, rather than each carrying its own copy
/// of a rule that only one of them had.
/// </para>
/// </remarks>
public static class RealTimeAccessPolicy
{
    private static readonly ConcurrentDictionary<string, Type?> TypeCache = new(StringComparer.Ordinal);

    /// <summary>
    /// Reduces an entity type name to the short form used in subscription group names.
    /// </summary>
    /// <remarks>
    /// Subscribers join under the name they asked for (<c>Invoice</c>) while an audit entry carries
    /// the assembly-qualified name (<c>MyApp.Domain.Invoice</c>). Both sides normalise here so the
    /// group a client joins is the group delivery targets.
    /// </remarks>
    public static string ToSubscriptionName(string entityTypeName)
    {
        if (string.IsNullOrWhiteSpace(entityTypeName)) return string.Empty;

        var name = entityTypeName.Split(',')[0].Trim();

        var lastDot = name.LastIndexOf('.');
        if (lastDot >= 0 && lastDot < name.Length - 1) name = name.Substring(lastDot + 1);

        // Nested types render as Outer+Inner.
        var plus = name.LastIndexOf('+');
        if (plus >= 0 && plus < name.Length - 1) name = name.Substring(plus + 1);

        return name;
    }

    /// <summary>
    /// Finds the entity type behind a name, by simple name or full name.
    /// </summary>
    public static Type? ResolveEntityType(string entityTypeName)
    {
        var simpleName = ToSubscriptionName(entityTypeName);
        if (string.IsNullOrEmpty(simpleName)) return null;

        return TypeCache.GetOrAdd(simpleName, name =>
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException)
                {
                    // One unloadable assembly must not fail every authorisation decision.
                    continue;
                }

                var match = types.FirstOrDefault(t =>
                    t.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                    || t.FullName?.Equals(name, StringComparison.OrdinalIgnoreCase) == true);

                if (match != null) return match;
            }

            return null;
        });
    }

    /// <summary>
    /// Whether <paramref name="user"/> may observe real-time events for <paramref name="entityTypeName"/>.
    /// </summary>
    /// <param name="reason">Why not, when the answer is <c>false</c>. Never shown to the client verbatim.</param>
    /// <remarks>
    /// Fails closed on a name this process cannot resolve: an unresolvable type has unknown access
    /// rules, and treating "no attribute found" as "no restriction" is how an unknown entity came to
    /// be granted a subscription. Declaring no roles is different from declaring unknown ones — an
    /// entity that names none is observable by any authenticated caller, which is what the schema
    /// says when it leaves <c>realTimeRoles</c> out.
    /// </remarks>
    public static bool MayObserve(ClaimsPrincipal? user, string entityTypeName, out string? reason)
    {
        var type = ResolveEntityType(entityTypeName);
        if (type is null)
        {
            reason = $"Unknown entity '{entityTypeName}'; cannot authorise access to its events.";
            return false;
        }

        var attribute = type.GetCustomAttribute<RealTimeAttribute>();

        if (attribute is { Enabled: false })
        {
            reason = $"Real-time events are disabled for '{ToSubscriptionName(entityTypeName)}'.";
            return false;
        }

        if (attribute is null || attribute.Roles.Length == 0)
        {
            reason = null;
            return true;
        }

        if (user?.Identity?.IsAuthenticated != true)
        {
            reason = $"'{ToSubscriptionName(entityTypeName)}' requires a role, and the caller is anonymous.";
            return false;
        }

        if (attribute.Roles.Any(user.IsInRole))
        {
            reason = null;
            return true;
        }

        reason = $"'{ToSubscriptionName(entityTypeName)}' requires one of: {string.Join(", ", attribute.Roles)}.";
        return false;
    }

    /// <summary>Convenience overload for call sites with nothing to report.</summary>
    public static bool MayObserve(ClaimsPrincipal? user, string entityTypeName)
        => MayObserve(user, entityTypeName, out _);

    /// <summary>
    /// Whether this caller may observe this event, checking the tenant as well as the role.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Roles were the only question asked, so a subscriber authenticated to one tenant received
    /// mutation events for every other: entity type, entity id, collection name, operator id and
    /// timestamp. Property diffs are empty on an insert, so field values did not cross, but which
    /// records exist elsewhere and when they change is not nothing.
    /// </para>
    /// <para>
    /// An event whose tenant is null is delivered only to a caller who also has none. That covers a
    /// single-tenant deployment, where neither side ever carries one, and refuses the case that
    /// matters: an entry written before audit entries carried a tenant cannot say whose it is, and
    /// showing it to whoever asks first is the failure this is here to prevent.
    /// </para>
    /// </remarks>
    public static bool MayObserve(
        ClaimsPrincipal? user, string entityTypeName, string? eventTenantId, out string? reason)
    {
        if (!MayObserve(user, entityTypeName, out reason)) return false;

        var callerTenant = TenantOf(user);
        if (string.Equals(Normalise(eventTenantId), Normalise(callerTenant), StringComparison.Ordinal))
        {
            reason = null;
            return true;
        }

        // The tenant is deliberately absent from the reason. It is logged where a subscriber may see
        // it, and naming another tenant's identifier in a message about refusing access to that
        // tenant's data would give away a smaller version of the same thing.
        reason = $"'{ToSubscriptionName(entityTypeName)}' belongs to another tenant.";
        return false;
    }

    /// <summary>The caller's tenant, by the same claims the tenant middleware reads.</summary>
    private static string? TenantOf(ClaimsPrincipal? user)
        => user?.FindFirst("tenant_id")?.Value ?? user?.FindFirst("tenantId")?.Value;

    private static string Normalise(string? tenant)
        => string.IsNullOrWhiteSpace(tenant) ? string.Empty : tenant.Trim();

    /// <summary>
    /// Whether this process can see the type behind a name at all.
    /// </summary>
    /// <remarks>
    /// Lets a transport tell a routine denial — the caller lacks the role — from a misconfiguration,
    /// where nothing can be resolved and so nothing is delivered. The second is silent by nature and
    /// deserves a louder log than the first, which is the system working.
    /// </remarks>
    public static bool IsKnownEntity(string entityTypeName) => ResolveEntityType(entityTypeName) is not null;
}
