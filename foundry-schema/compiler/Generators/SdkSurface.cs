using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Foundry.Schema.Compiler.Generators;

/// <summary>
/// What a client SDK should expose for an entity. Shared by all three language generators.
/// </summary>
/// <remarks>
/// <para>
/// The three generators each decided this for themselves and all three decided it the same wrong
/// way: every entity got <c>getAll</c>, <c>getById</c>, <c>create</c> and <c>delete</c> whatever its
/// <c>apiEnabledMethods</c> said, and none of them got <c>update</c> even though four of the five
/// entities in the showcase declare PUT. So the SDKs offered methods that answer 405 and omitted one
/// the API serves.
/// </para>
/// <para>
/// Deciding it once means a fix in one language is a fix in all three, which is the opposite of how
/// the route rule went: it was wrong here in all three languages long after it had been corrected in
/// the exporters and in Studio.
/// </para>
/// </remarks>
internal static class SdkSurface
{
    /// <summary>The HTTP methods this entity actually serves.</summary>
    internal static List<string> MethodsFor(Entity entity) => ApiManifestGenerator.EnabledMethods(entity);

    internal static bool HasList(Entity entity) => MethodsFor(entity).Contains("GET");
    internal static bool HasGetById(Entity entity) => MethodsFor(entity).Contains("GET_BY_ID");
    internal static bool HasCreate(Entity entity) => MethodsFor(entity).Contains("POST");
    internal static bool HasUpdate(Entity entity) => MethodsFor(entity).Contains("PUT");
    internal static bool HasDelete(Entity entity) => MethodsFor(entity).Contains("DELETE");

    /// <summary>Whether an entity is worth emitting a client for at all.</summary>
    internal static bool HasAnySurface(Entity entity) => MethodsFor(entity).Count > 0;

    /// <summary>
    /// Whether a caller has to supply this property.
    /// </summary>
    /// <remarks>
    /// Only the key used to be optional, so a caller had to construct every field to satisfy the
    /// type — including the ones the server assigns and refuses to take from a request body. The
    /// tenant key is stamped from the caller's token, the owner key from their subject, and the id is
    /// generated; a client that demands them is asking for values the API will overwrite or reject.
    /// </remarks>
    internal static bool IsCallerSupplied(Property property)
        => !property.IsKey
           && !property.IsTenantKey
           && !property.IsOwnerKey
           && !property.IsSharedWithKey
           && !property.Attributes.Contains("TenantKey");

    /// <summary>Whether a caller-supplied property is mandatory.</summary>
    internal static bool IsRequired(Property property)
        => IsCallerSupplied(property)
           && (property.Attributes.Contains("Required") || DefaultIsOutOfRange(property));

    /// <summary>
    /// Whether omitting this property is refused anyway, because the value it binds to is out of range.
    /// </summary>
    /// <remarks>
    /// A non-nullable number left out of a body binds 0, and a <c>Range</c> that excludes 0 then
    /// refuses the request. <c>BusinessUnit.PriorityWeight</c>, <c>Range(1, 10)</c>, was optional in
    /// every SDK and mandatory on the server, so the first entity any bootstrap creates failed at step
    /// one. The schema never said <c>Required</c>; the range said it for it.
    /// </remarks>
    private static bool DefaultIsOutOfRange(Property property)
    {
        if (!NumericTypes.Contains(property.Type)) return false;

        foreach (var attribute in property.Attributes)
        {
            var match = RangePattern.Match(attribute);
            if (!match.Success) continue;

            // The pattern admits "1.2.3"; such a range is the validator's to report, not this to crash on.
            if (!decimal.TryParse(match.Groups[1].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var min)
                || !decimal.TryParse(match.Groups[2].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var max))
                continue;

            if (min > 0 || max < 0) return true;
        }

        return false;
    }

    // Exact names: a nullable "int?" binds null when omitted, not 0, so it is not caught by this.
    private static readonly HashSet<string> NumericTypes = new(StringComparer.OrdinalIgnoreCase)
        { "int", "int32", "long", "decimal", "double", "float" };

    private static readonly Regex RangePattern = new(
        @"^Range\(\s*(-?[0-9.]+)\s*,\s*(-?[0-9.]+)\s*\)$", RegexOptions.CultureInvariant);

    /// <summary>Properties a caller may send, in declaration order.</summary>
    internal static IEnumerable<Property> CallerProperties(Entity entity)
        => (entity.Properties ?? new List<Property>()).Where(IsCallerSupplied);
}
