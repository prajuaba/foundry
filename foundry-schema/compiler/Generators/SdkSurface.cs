using System.Collections.Generic;
using System.Linq;

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
        => IsCallerSupplied(property) && property.Attributes.Contains("Required");

    /// <summary>Properties a caller may send, in declaration order.</summary>
    internal static IEnumerable<Property> CallerProperties(Entity entity)
        => (entity.Properties ?? new List<Property>()).Where(IsCallerSupplied);
}
