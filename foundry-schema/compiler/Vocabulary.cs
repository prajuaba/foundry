using System;
using System.Collections.Generic;
using System.Linq;

namespace Foundry.Schema.Compiler
{
    /// <summary>
    /// How an attribute is written in the IR.
    /// </summary>
    public enum AttributeArity
    {
        /// <summary>Written bare, e.g. <c>"Required"</c>.</summary>
        Bare,

        /// <summary>Written with arguments, e.g. <c>"MinLength(3)"</c>.</summary>
        Parameterised
    }

    /// <summary>
    /// One entry in the supported attribute vocabulary.
    /// </summary>
    public sealed record AttributeSpec
    {
        /// <summary>Canonical attribute name as written in the IR.</summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>Alternative spellings accepted by the compiler.</summary>
        public IReadOnlyList<string> Aliases { get; init; } = Array.Empty<string>();

        /// <summary>Whether the attribute takes arguments.</summary>
        public AttributeArity Arity { get; init; } = AttributeArity.Bare;

        /// <summary>What the attribute does, in terms the schema author cares about.</summary>
        public string Summary { get; init; } = string.Empty;

        /// <summary>An example of correct usage in the IR.</summary>
        public string Example { get; init; } = string.Empty;

        /// <summary>True when the attribute is valid on DTO properties as well as entity properties.</summary>
        public bool ValidOnDtos { get; init; } = true;
    }

    /// <summary>
    /// The single source of truth for the IR's type and attribute vocabulary.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both <see cref="SchemaValidator"/> and the <c>foundry ai-spec</c> bundle generator read
    /// this class. That is deliberate: the vocabulary a model is told about and the vocabulary
    /// the compiler actually honours are the same list, so the AI spec cannot drift from the
    /// compiler's behaviour the way a hand-written prompt does.
    /// </para>
    /// <para>
    /// Adding support for a new attribute means adding it here <em>and</em> handling it in
    /// <see cref="PocoGenerator"/>. The vocabulary round-trip test asserts the two agree.
    /// </para>
    /// </remarks>
    public static class Vocabulary
    {
        /// <summary>
        /// Scalar types the compiler maps to a known C# type. Keys are the IR spelling
        /// (compared case-insensitively); values are the emitted C# type.
        /// </summary>
        public static readonly IReadOnlyDictionary<string, string> ScalarTypes =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["string"] = "string",
                ["int"] = "int",
                ["long"] = "long",
                ["decimal"] = "decimal",
                ["double"] = "double",
                ["float"] = "float",
                ["bool"] = "bool",
                ["DateTime"] = "DateTime",
                ["DateOnly"] = "DateOnly",
                ["TimeOnly"] = "TimeOnly",
                ["Guid"] = "Guid",
                ["ObjectId"] = "ObjectId"
            };

        /// <summary>
        /// Types that are valid for a property marked <c>isKey</c>.
        /// </summary>
        /// <remarks>
        /// Only <c>ObjectId</c>. This list previously also advertised <c>Guid</c>, <c>string</c>,
        /// <c>int</c> and <c>long</c>, none of which work: <c>IRepository&lt;T&gt;</c> in the MongoDB
        /// data layer is constrained to <c>IEntity&lt;ObjectId&gt;</c>, so an entity keyed on anything
        /// else generates a type that compiles but has no resolvable repository — it cannot be
        /// persisted, queried or served. Widening this set again requires widening that constraint
        /// first.
        /// </remarks>
        public static readonly IReadOnlySet<string> KeyTypes =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ObjectId" };

        /// <summary>
        /// HTTP methods accepted on a custom endpoint.
        /// </summary>
        public static readonly IReadOnlySet<string> HttpMethods =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "GET", "POST", "PUT", "PATCH", "DELETE" };

        /// <summary>
        /// Operation kinds accepted on a custom endpoint.
        /// </summary>
        public static readonly IReadOnlySet<string> OperationTypes =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Query", "Insert", "Update", "Custom" };

        /// <summary>
        /// Connector transport kinds.
        /// </summary>
        public static readonly IReadOnlySet<string> ConnectorTypes =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "REST", "SOAP", "GraphQL" };

        /// <summary>
        /// Connector authentication kinds.
        /// </summary>
        public static readonly IReadOnlySet<string> AuthTypes =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "None", "Basic", "ApiKey", "Bearer", "OAuth2" };

        /// <summary>
        /// Every property attribute the compiler honours.
        /// </summary>
        public static readonly IReadOnlyList<AttributeSpec> Attributes = new List<AttributeSpec>
        {
            new()
            {
                Name = "Required",
                Summary = "Emits [Required] and makes the C# property 'required', so it must be supplied.",
                Example = "\"attributes\": [\"Required\"]"
            },
            new()
            {
                Name = "Unique",
                Aliases = new[] { "UniqueIndex" },
                Summary = "Creates a unique MongoDB index on the property.",
                Example = "\"attributes\": [\"Unique\"]",
                ValidOnDtos = false
            },
            new()
            {
                Name = "Indexed",
                Aliases = new[] { "Index" },
                Summary = "Creates a non-unique MongoDB index on the property.",
                Example = "\"attributes\": [\"Indexed\"]",
                ValidOnDtos = false
            },
            new()
            {
                Name = "TextIndex",
                Summary = "Includes the property in the entity's full-text search index.",
                Example = "\"attributes\": [\"TextIndex\"]",
                ValidOnDtos = false
            },
            new()
            {
                Name = "TenantKey",
                Summary = "Marks the property as the tenant discriminator. Requires the entity to set multiTenant: true.",
                Example = "\"attributes\": [\"TenantKey\"]",
                ValidOnDtos = false
            },
            new()
            {
                Name = "Encrypt",
                Summary = "Encrypts the property at rest with AES-256-GCM via the KMS envelope key.",
                Example = "\"attributes\": [\"Encrypt\"]",
                ValidOnDtos = false
            },
            new()
            {
                Name = "Mask",
                Summary = "Irreversibly masks the property in logs and API responses.",
                Example = "\"attributes\": [\"Mask\"]",
                ValidOnDtos = false
            },
            new()
            {
                Name = "MaskEmail",
                Summary = "Masks the property using email-shaped masking, e.g. j***@domain.com.",
                Example = "\"attributes\": [\"MaskEmail\"]",
                ValidOnDtos = false
            },
            new()
            {
                Name = "PiiEmail",
                Summary = "Tags the property as personally identifiable email data for audit reporting.",
                Example = "\"attributes\": [\"PiiEmail\"]",
                ValidOnDtos = false
            },
            new()
            {
                Name = "PiiCreditCard",
                Summary = "Tags the property as payment card data for audit reporting.",
                Example = "\"attributes\": [\"PiiCreditCard\"]",
                ValidOnDtos = false
            },
            new()
            {
                Name = "MinLength",
                Arity = AttributeArity.Parameterised,
                Summary = "Minimum string length. Takes one integer argument.",
                Example = "\"attributes\": [\"MinLength(3)\"]"
            },
            new()
            {
                Name = "MaxLength",
                Arity = AttributeArity.Parameterised,
                Summary = "Maximum string length. Takes one integer argument.",
                Example = "\"attributes\": [\"MaxLength(120)\"]"
            },
            new()
            {
                Name = "Range",
                Arity = AttributeArity.Parameterised,
                Summary = "Inclusive numeric range. Takes two numeric arguments.",
                Example = "\"attributes\": [\"Range(0, 100)\"]"
            },
            new()
            {
                Name = "Regex",
                Arity = AttributeArity.Parameterised,
                Summary = "Emits a compiled [GeneratedRegex] validation pattern. Takes one quoted string argument.",
                Example = "\"attributes\": [\"Regex(\\\"^[A-Z]{2}-\\\\\\\\d{4}$\\\")\"]"
            },
            new()
            {
                Name = "Email",
                Summary = "Validates the property as an email address.",
                Example = "\"attributes\": [\"Email\"]"
            },
            new()
            {
                Name = "Url",
                Summary = "Validates the property as an absolute URL.",
                Example = "\"attributes\": [\"Url\"]"
            },
            new()
            {
                Name = "Phone",
                Summary = "Validates the property as a telephone number.",
                Example = "\"attributes\": [\"Phone\"]"
            }
        };

        private static readonly IReadOnlyDictionary<string, AttributeSpec> AttributeLookup = BuildAttributeLookup();

        private static IReadOnlyDictionary<string, AttributeSpec> BuildAttributeLookup()
        {
            var map = new Dictionary<string, AttributeSpec>(StringComparer.OrdinalIgnoreCase);
            foreach (var spec in Attributes)
            {
                map[spec.Name] = spec;
                foreach (var alias in spec.Aliases)
                    map[alias] = spec;
            }
            return map;
        }

        /// <summary>
        /// Resolves an attribute as written in the IR to its specification.
        /// </summary>
        /// <param name="attribute">
        /// The raw IR attribute text, with or without an argument list.
        /// </param>
        /// <param name="spec">The resolved specification, when found.</param>
        /// <returns>True when the attribute is part of the supported vocabulary.</returns>
        public static bool TryResolveAttribute(string? attribute, out AttributeSpec? spec)
        {
            spec = null;
            if (string.IsNullOrWhiteSpace(attribute)) return false;

            var text = attribute!.Trim();
            var parenIndex = text.IndexOf('(');
            var name = parenIndex < 0 ? text : text.Substring(0, parenIndex);

            return AttributeLookup.TryGetValue(name.Trim(), out spec);
        }

        /// <summary>
        /// Maps an IR type name to its C# equivalent, passing unknown names through unchanged.
        /// </summary>
        public static string MapType(string? schemaType)
        {
            if (string.IsNullOrWhiteSpace(schemaType)) return "string";
            return ScalarTypes.TryGetValue(schemaType!.Trim(), out var mapped) ? mapped : schemaType!.Trim();
        }

        /// <summary>
        /// Returns true when the IR type name maps to a known scalar type.
        /// </summary>
        public static bool IsKnownScalar(string? schemaType)
            => !string.IsNullOrWhiteSpace(schemaType) && ScalarTypes.ContainsKey(schemaType!.Trim());

        /// <summary>
        /// Canonical attribute names, for documentation and the AI skill bundle.
        /// </summary>
        public static IEnumerable<string> AttributeNames => Attributes.Select(a => a.Name);
    }
}
