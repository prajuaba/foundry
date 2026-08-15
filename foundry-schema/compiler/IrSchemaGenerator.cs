using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Foundry.Schema.Compiler
{
    /// <summary>
    /// Generates a JSON Schema (Draft 2020-12) describing the Foundry IR document format.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The schema is derived by reflecting over <see cref="SchemaModel"/> rather than being
    /// hand-written. That is the whole point: a hand-maintained description of the IR drifts
    /// from the compiler the moment someone adds a property, and the previous AI integration
    /// drifted exactly that way — its prompt described 7 rules covering entities, enums and
    /// properties while silently omitting DTOs, endpoints, workflows, connectors, tenancy,
    /// Kafka and FileIO.
    /// </para>
    /// <para>
    /// The output serves two consumers at once:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// Documentation and editor tooling, via <c>$schema</c> association.
    /// </description></item>
    /// <item><description>
    /// Grammar-constrained decoding. Ollama accepts a JSON Schema in its <c>format</c> field and
    /// compiles it to a sampling grammar. Every constraint added here becomes a token the model
    /// physically cannot emit — which is why identifier patterns are attached to name fields and
    /// <c>additionalProperties</c> is closed everywhere. A model constrained by this schema
    /// cannot produce the <c>httpMethod</c>-instead-of-<c>method</c> mistake in the old showcase
    /// sample, and cannot emit an identifier that would trip FDY4001.
    /// </description></item>
    /// </list>
    /// </remarks>
    public static class IrSchemaGenerator
    {
        /// <summary>The dialect the emitted schema declares.</summary>
        public const string Dialect = "https://json-schema.org/draft/2020-12/schema";

        /// <summary>
        /// Regex source matching a valid, non-reserved C# identifier.
        /// </summary>
        /// <remarks>
        /// Reserved keywords cannot be excluded in a regex the way <see cref="CodeGen.Ident"/>
        /// does, so the validator still owns FDY4002. This pattern closes the far more dangerous
        /// case: punctuation and braces that let a name escape its emission context.
        /// </remarks>
        public const string IdentifierPattern = "^[A-Za-z_][A-Za-z0-9_]*$";

        /// <summary>Regex source matching a dotted C# namespace.</summary>
        /// <remarks>
        /// Deliberately looser than the validator's rule (it permits a trailing or doubled dot)
        /// because it must survive GBNF conversion — see <see cref="GrammarUnsafeTokens"/>. The
        /// strict check remains FDY4003 in <see cref="SchemaValidator"/>; this pattern's job is
        /// only to keep punctuation, whitespace and braces out of a namespace at the sampler level.
        /// </remarks>
        public const string NamespacePattern = "^[A-Za-z_][A-Za-z0-9_.]*$";

        /// <summary>Regex source matching an absolute route path.</summary>
        /// <remarks>The hyphen is last in the class so it needs no escape.</remarks>
        public const string RoutePattern = "^/[A-Za-z0-9_./{}-]*$";

        /// <summary>
        /// Regex constructs that Ollama's JSON-Schema-to-GBNF converter cannot compile.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Established empirically against Ollama 0.32.3: <c>\d</c> and <c>\s</c> shorthand classes
        /// are rejected, and an unanchored pattern is rejected. Escaped <c>\.</c> and <c>\(</c>,
        /// braces inside a character class, negated classes, quantified groups and nested optional
        /// groups all convert fine.
        /// </para>
        /// <para>
        /// Every pattern emitted into the IR schema must avoid these, because a single
        /// unconvertible pattern fails the whole request with
        /// "failed to initialize samplers: failed to parse grammar" — the grammar is all-or-nothing.
        /// <see cref="FindGrammarUnsafePatterns"/> guards this and is asserted by test.
        /// </para>
        /// </remarks>
        public static readonly IReadOnlyList<string> GrammarUnsafeTokens = new[] { "\\d", "\\s", "\\w", "\\b" };

        // Properties that exist only as a serialisation alias for another property. Emitting both
        // would present the model two spellings for one field and enlarge the grammar for nothing.
        private static readonly HashSet<string> AliasProperties = new(StringComparer.Ordinal)
        {
            "Index.IsUnique"
        };

        /// <summary>
        /// Builds the regex source that constrains a property attribute string.
        /// </summary>
        /// <remarks>
        /// Derived from <see cref="Vocabulary.Attributes"/> and the argument grammar enforced by
        /// <see cref="CodeGen.TryParseAttribute"/>, so the sampler, the validator and the emitter
        /// all agree on what an attribute may look like.
        /// </remarks>
        public static string BuildAttributePattern()
        {
            var bare = new List<string>();
            var parameterised = new List<string>();

            foreach (var spec in Vocabulary.Attributes)
            {
                var names = new List<string> { spec.Name };
                names.AddRange(spec.Aliases);

                foreach (var name in names)
                {
                    if (spec.Arity == AttributeArity.Bare)
                    {
                        bare.Add(name);
                        continue;
                    }

                    // [0-9] rather than \d, and a literal space rather than \s: both shorthand
                    // classes break GBNF conversion. See GrammarUnsafeTokens.
                    parameterised.Add(name switch
                    {
                        "Range" => $"{name}\\(-?[0-9]+(\\.[0-9]+)?, *-?[0-9]+(\\.[0-9]+)?\\)",
                        "Regex" => $"{name}\\(\"[^\"\\\\{{}}]*\"\\)",
                        _ => $"{name}\\([0-9]+\\)"
                    });
                }
            }

            var alternatives = bare.Concat(parameterised);
            return $"^({string.Join("|", alternatives)})$";
        }

        /// <summary>
        /// Finds every <c>pattern</c> in the generated schema that Ollama's grammar converter
        /// would reject.
        /// </summary>
        /// <returns>
        /// A list of <c>"jsonPath: pattern — reason"</c> entries. Empty means the schema is safe to
        /// pass as a constrained-decoding format.
        /// </returns>
        /// <remarks>
        /// Grammar compilation is all-or-nothing: one bad pattern fails the entire request. Because
        /// the patterns are assembled from <see cref="Vocabulary"/>, adding a new parameterised
        /// attribute could silently reintroduce an unsupported construct, so this is asserted by test
        /// rather than left to manual review.
        /// </remarks>
        public static IReadOnlyList<string> FindGrammarUnsafePatterns()
        {
            var problems = new List<string>();
            using var doc = JsonDocument.Parse(Generate());
            Walk(doc.RootElement, "$", problems);
            return problems;

            static void Walk(JsonElement element, string path, List<string> problems)
            {
                switch (element.ValueKind)
                {
                    case JsonValueKind.Object:
                        foreach (var prop in element.EnumerateObject())
                        {
                            if (prop.NameEquals("pattern") && prop.Value.ValueKind == JsonValueKind.String)
                            {
                                var pattern = prop.Value.GetString() ?? "";

                                foreach (var token in GrammarUnsafeTokens)
                                {
                                    if (pattern.Contains(token, StringComparison.Ordinal))
                                        problems.Add($"{path}.pattern: {pattern} — contains unsupported shorthand '{token}'");
                                }

                                if (!pattern.StartsWith("^", StringComparison.Ordinal)
                                    || !pattern.EndsWith("$", StringComparison.Ordinal))
                                {
                                    problems.Add($"{path}.pattern: {pattern} — must be anchored with ^ and $");
                                }
                            }

                            Walk(prop.Value, $"{path}.{prop.Name}", problems);
                        }
                        break;

                    case JsonValueKind.Array:
                        var index = 0;
                        foreach (var item in element.EnumerateArray())
                            Walk(item, $"{path}[{index++}]", problems);
                        break;
                }
            }
        }

        /// <summary>
        /// Generates the IR JSON Schema as indented JSON text.
        /// </summary>
        public static string Generate()
        {
            var defs = new Dictionary<string, object?>();
            var rootRef = BuildTypeSchema(typeof(SchemaModel), defs);

            var root = new Dictionary<string, object?>
            {
                ["$schema"] = Dialect,
                ["$id"] = "https://foundry.dev/schemas/foundry.ir.schema.json",
                ["title"] = "Foundry IR document",
                ["description"] =
                    "The normative Foundry intermediate representation. A Foundry domain is authored "
                    + "as one of these documents; the compiler turns it into C#. Hand-written business "
                    + "logic belongs in *.Custom.cs files, never in this document.",
                ["$defs"] = defs
            };

            // Inline the root object's members so the document reads top-down.
            if (rootRef.TryGetValue("$ref", out var refValue)
                && refValue is string refPath
                && defs.TryGetValue(refPath.Replace("#/$defs/", ""), out var rootDef)
                && rootDef is Dictionary<string, object?> rootObject)
            {
                foreach (var pair in rootObject)
                {
                    if (pair.Key is "title" or "description") continue;
                    root[pair.Key] = pair.Value;
                }
                defs.Remove(refPath.Replace("#/$defs/", ""));
            }

            return JsonSerializer.Serialize(root, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
        }

        private static Dictionary<string, object?> BuildTypeSchema(Type type, Dictionary<string, object?> defs)
        {
            var name = type.Name;

            if (!defs.ContainsKey(name))
            {
                // Reserve the slot before recursing so a self-referential type terminates.
                defs[name] = new Dictionary<string, object?>();

                var properties = new Dictionary<string, object?>();
                var required = new List<string>();

                foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (AliasProperties.Contains($"{type.Name}.{prop.Name}")) continue;

                    var jsonName = GetJsonName(prop);
                    properties[jsonName] = BuildPropertySchema(type, prop, defs);

                    if (IsRequired(type, prop))
                        required.Add(jsonName);
                }

                var schema = new Dictionary<string, object?>
                {
                    ["type"] = "object",
                    ["description"] = DescribeType(type),
                    ["properties"] = properties,
                    // Closed objects are what stop a model inventing a plausible-but-ignored field.
                    ["additionalProperties"] = false
                };

                if (required.Count > 0)
                    schema["required"] = required;

                defs[name] = schema;
            }

            return new Dictionary<string, object?> { ["$ref"] = $"#/$defs/{name}" };
        }

        private static object BuildPropertySchema(Type owner, PropertyInfo prop, Dictionary<string, object?> defs)
        {
            var schema = BuildValueSchema(prop.PropertyType, defs);

            // Attach vocabulary and safety constraints. These are the constraints that turn the
            // grammar into a correctness guarantee rather than a formatting hint.
            var key = $"{owner.Name}.{prop.Name}";
            switch (key)
            {
                case "SchemaModel.Namespace":
                    schema["pattern"] = NamespacePattern;
                    schema["description"] = "Dotted C# namespace for the generated code, e.g. 'Acme.Billing'.";
                    break;

                case "Entity.Name":
                case "Enum.Name":
                case "DtoModel.Name":
                case "ConnectorModel.Name":
                    schema["pattern"] = IdentifierPattern;
                    schema["description"] = "PascalCase C# identifier. No spaces or punctuation.";
                    break;

                case "Property.Name":
                case "DtoProperty.Name":
                    schema["pattern"] = IdentifierPattern;
                    schema["description"] = "PascalCase C# identifier for the property.";
                    break;

                case "Entity.BaseClass":
                    schema["description"] = "Optional base type name. Leave unset to derive from BaseEntity<TKey>.";
                    break;

                case "Property.Type":
                case "DtoProperty.Type":
                    schema["description"] =
                        $"One of the scalar types ({string.Join(", ", Vocabulary.ScalarTypes.Keys)}), "
                        + "or the name of an enum declared in 'enums' (in which case set isEnum: true).";
                    break;

                case "Property.Attributes":
                case "DtoProperty.Attributes":
                    schema["items"] = new Dictionary<string, object?>
                    {
                        ["type"] = "string",
                        ["pattern"] = BuildAttributePattern(),
                        ["description"] = string.Join("; ", Vocabulary.Attributes.Select(a => $"{a.Name}: {a.Summary}"))
                    };
                    break;

                case "CustomEndpoint.Method":
                    schema["enum"] = Vocabulary.HttpMethods.OrderBy(m => m).ToList();
                    break;

                case "CustomEndpoint.OperationType":
                    schema["enum"] = Vocabulary.OperationTypes.OrderBy(m => m).ToList();
                    break;

                case "CustomEndpoint.Route":
                    schema["pattern"] = RoutePattern;
                    schema["description"] = "Absolute route path beginning with '/', e.g. '/api/v1/orders/submit'.";
                    break;

                case "CustomEndpoint.RequestType":
                    schema["pattern"] = IdentifierPattern;
                    break;

                case "ConnectorModel.Type":
                    schema["enum"] = Vocabulary.ConnectorTypes.OrderBy(m => m).ToList();
                    break;

                case "ConnectorModel.AuthType":
                    schema["enum"] = Vocabulary.AuthTypes.OrderBy(m => m).ToList();
                    break;

                case "WorkflowTransitionModel.Trigger":
                    schema["pattern"] = IdentifierPattern;
                    schema["description"] =
                        "PascalCase command name generated for this transition. Must be unique across all workflows.";
                    break;

                case "Enum.Values":
                    schema["items"] = new Dictionary<string, object?>
                    {
                        ["type"] = "string",
                        ["pattern"] = IdentifierPattern
                    };
                    break;

                case "Entity.ArchiveThresholdYears":
                    schema["minimum"] = 1;
                    break;

                case "Entity.Description":
                case "Property.Description":
                case "DtoModel.Description":
                case "DtoProperty.Description":
                case "CustomEndpoint.Description":
                case "Enum.Description":
                case "WorkflowModel.Description":
                case "ConnectorModel.Description":
                    schema["description"] = "Optional prose: free-form text authored in the schema, carried through to generated code and published API documentation. No effect on behavior.";
                    break;
            }

            return schema;
        }

        private static Dictionary<string, object?> BuildValueSchema(Type type, Dictionary<string, object?> defs)
        {
            var underlying = Nullable.GetUnderlyingType(type) ?? type;

            if (underlying == typeof(string))
                return new Dictionary<string, object?> { ["type"] = "string" };

            if (underlying == typeof(bool))
                return new Dictionary<string, object?> { ["type"] = "boolean" };

            if (underlying == typeof(int) || underlying == typeof(long))
                return new Dictionary<string, object?> { ["type"] = "integer" };

            if (underlying == typeof(double) || underlying == typeof(float) || underlying == typeof(decimal))
                return new Dictionary<string, object?> { ["type"] = "number" };

            if (underlying.IsGenericType)
            {
                var generic = underlying.GetGenericTypeDefinition();
                var args = underlying.GetGenericArguments();

                if (generic == typeof(List<>) || generic == typeof(IReadOnlyList<>) || generic == typeof(IList<>))
                {
                    return new Dictionary<string, object?>
                    {
                        ["type"] = "array",
                        ["items"] = BuildValueSchema(args[0], defs)
                    };
                }

                if (generic == typeof(Dictionary<,>) || generic == typeof(IReadOnlyDictionary<,>))
                {
                    return new Dictionary<string, object?>
                    {
                        ["type"] = "object",
                        ["additionalProperties"] = BuildValueSchema(args[1], defs)
                    };
                }
            }

            // A nested IR record.
            if (underlying.IsClass && underlying.Namespace == typeof(SchemaModel).Namespace)
                return BuildTypeSchema(underlying, defs);

            // Anything else is opaque; permit any JSON rather than silently constraining it wrongly.
            return new Dictionary<string, object?>();
        }

        private static string GetJsonName(PropertyInfo prop)
        {
            var attribute = prop.GetCustomAttribute<JsonPropertyNameAttribute>();
            if (attribute != null) return attribute.Name;

            var name = prop.Name;
            return char.ToLowerInvariant(name[0]) + name.Substring(1);
        }

        /// <summary>
        /// Determines whether a property is semantically required.
        /// </summary>
        /// <remarks>
        /// Kept deliberately minimal. Under constrained decoding the model must emit every
        /// required field, so marking optional configuration as required would force it to
        /// produce noise. These are exactly the fields whose absence the FDY1xxx diagnostics
        /// treat as an error.
        /// </remarks>
        private static bool IsRequired(Type owner, PropertyInfo prop)
            => $"{owner.Name}.{prop.Name}" switch
            {
                "SchemaModel.Namespace" => true,
                "SchemaModel.Entities" => true,
                "Entity.Name" => true,
                "Entity.Properties" => true,
                "Property.Name" => true,
                "Property.Type" => true,
                "Enum.Name" => true,
                "Enum.Values" => true,
                "DtoModel.Name" => true,
                "DtoProperty.Name" => true,
                "DtoProperty.Type" => true,

                // Custom endpoints carry silent defaults that turn an omission into wrong output
                // rather than an error: Method defaults to "GET", so an omitted method silently
                // becomes a GET even when the request said POST, and an empty RequestType makes
                // the emitter skip the handler entirely. Requiring them means the grammar will not
                // let the model leave them out.
                "CustomEndpoint.Route" => true,
                "CustomEndpoint.Method" => true,
                "CustomEndpoint.RequestType" => true,
                "CustomEndpoint.TargetEntity" => true,
                "CustomEndpoint.OperationType" => true,

                _ => false
            };

        private static string DescribeType(Type type) => type.Name switch
        {
            nameof(SchemaModel) => "Root of a Foundry IR document.",
            nameof(Entity) =>
                "A persisted domain entity. Generates a record, a MongoDB repository, CRUD endpoints, "
                + "and any opted-in infrastructure (Kafka outbox, FileIO service, real-time push).",
            nameof(Property) => "A property on an entity. Exactly one property per entity must set isKey.",
            nameof(Index) => "A MongoDB index over one or more of the entity's own properties.",
            nameof(Enum) => "A C# enum emitted into the domain namespace.",
            nameof(DtoModel) => "A data-transfer object, optionally projected from entity properties.",
            nameof(DtoProperty) => "A property on a DTO. Set sourceEntity/sourceProperty to project from an entity.",
            nameof(CustomEndpoint) => "A hand-shaped API endpoint beyond generated CRUD.",
            nameof(AssignmentRule) => "Maps a request field onto an entity property for Update operations.",
            nameof(WorkflowModel) => "A versioned state machine bound to one entity.",
            nameof(WorkflowStateModel) => "A workflow state. Exactly one state must set isInitial.",
            nameof(WorkflowTransitionModel) => "A transition between workflow states; generates a command type.",
            nameof(WorkflowConditionModel) => "A guard evaluated before a transition or choice branch is taken.",
            nameof(WorkflowActionModel) => "A side effect performed when a transition fires.",
            nameof(WorkflowChoiceNodeModel) => "A decision gate that routes to one of several states by condition.",
            nameof(WorkflowBranchModel) => "One conditional branch out of a choice node.",
            nameof(ConnectorModel) => "An outbound connector to an external REST, SOAP or GraphQL service.",
            _ => type.Name
        };
    }
}
