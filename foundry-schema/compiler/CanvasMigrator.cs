using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Foundry.Schema.Compiler
{
    /// <summary>
    /// Converts a Studio canvas document into normative IR.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A canvas document describes a diagram: <c>nodes</c> with positions, and <c>edges</c> between
    /// them. The compiler consumes IR — <c>entities</c> and <c>enums</c> — and rejects a canvas file
    /// outright with <see cref="DiagnosticCatalog.StudioCanvasDocument"/>, whose hint has always told
    /// the reader to run <c>foundry migrate</c>. That command did not exist, so the one diagnostic
    /// that explains the difference between the two formats ended in an instruction that could not be
    /// followed.
    /// </para>
    /// <para>
    /// Two canvas shapes are accepted, because two have been shipped. Studio currently nests the
    /// entity under <c>data.entity</c> with camelCase fields; earlier versions — and the VS Code
    /// extension's "New Schema" command, which was never updated — put the fields directly on
    /// <c>data</c> in PascalCase. Both are read, because a migration tool that only understands the
    /// current format is no use to the documents that need migrating.
    /// </para>
    /// </remarks>
    public static class CanvasMigrator
    {
        /// <summary>Serialisation options that produce the normative IR field names.</summary>
        /// <remarks>
        /// CamelCase, with <c>[JsonPropertyName]</c> winning where a field declares one — exactly the
        /// rule <c>IrSchemaGenerator.GetJsonName</c> applies when it publishes the JSON Schema, so
        /// migrated output matches the schema the document will be checked against.
        /// </remarks>
        public static readonly JsonSerializerOptions IrOutputOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private static readonly JsonSerializerOptions ReadOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        /// <summary>Whether <paramref name="json"/> looks like a canvas document rather than IR.</summary>
        public static bool IsCanvasDocument(string json)
        {
            try
            {
                var root = JsonNode.Parse(json);
                return root is JsonObject obj
                    && obj.TryGetPropertyValue("nodes", out var nodes)
                    && nodes is JsonArray;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        /// <summary>
        /// Converts a canvas document to a schema model.
        /// </summary>
        /// <exception cref="InvalidOperationException">The document is not a canvas document.</exception>
        public static SchemaModel Migrate(string json)
        {
            var root = JsonNode.Parse(json) as JsonObject
                ?? throw new InvalidOperationException("The document is not a JSON object.");

            if (root["nodes"] is not JsonArray nodes)
            {
                throw new InvalidOperationException(
                    "The document has no 'nodes' array, so it is not a Studio canvas document. "
                    + "If it is already IR, it needs no migration.");
            }

            var entities = new List<Entity>();
            var enums = new List<Enum>();

            foreach (var node in nodes.OfType<JsonObject>())
            {
                var kind = Value(node, "type");
                var data = node["data"] as JsonObject;
                if (data is null) continue;

                if (string.Equals(kind, "enumNode", StringComparison.OrdinalIgnoreCase))
                {
                    var source = Nested(data, "enum") ?? data;
                    var model = Deserialize<Enum>(source);
                    if (model is not null && !string.IsNullOrWhiteSpace(model.Name)) enums.Add(model);
                }
                else
                {
                    // Anything that is not an enum node is treated as an entity, including a node
                    // whose `type` is missing. A diagram that cannot say what a node is still holds
                    // the entity's fields, and dropping it silently would lose domain the user drew.
                    var source = Nested(data, "entity") ?? data;
                    var model = Deserialize<Entity>(source);
                    if (model is not null && !string.IsNullOrWhiteSpace(model.Name)) entities.Add(model);
                }
            }

            return new SchemaModel
            {
                Namespace = Value(root, "namespace") ?? "Domain",
                Version = Value(root, "version") ?? "1.0.0",
                Entities = entities,
                Enums = enums.Count > 0 ? enums : DeserializeList<Enum>(root, "enums"),

                // Carried across as-is. These live at the top level of a canvas document already —
                // the canvas only ever diagrammed entities and enums — so they are IR already and
                // re-deriving them would be a second implementation of the same mapping.
                Dtos = DeserializeList<DtoModel>(root, "dtos"),
                CustomEndpoints = DeserializeList<CustomEndpoint>(root, "customEndpoints"),
                Workflows = DeserializeList<WorkflowModel>(root, "workflows"),
                Connectors = DeserializeList<ConnectorModel>(root, "connectors")
            };
        }

        /// <summary>Converts a canvas document to normative IR JSON.</summary>
        /// <remarks>
        /// The result is trimmed of defaults. A migrated document is the file its author edits next,
        /// and a two-entity domain serialised in full is two hundred lines of <c>false</c> and
        /// <c>[]</c> with the domain buried inside it. Every boolean in this IR defaults to false and
        /// every collection to empty, so dropping them says exactly what the full form said.
        /// </remarks>
        public static string MigrateToJson(string json)
        {
            var node = JsonSerializer.SerializeToNode(Migrate(json), IrOutputOptions);
            if (node is JsonObject root) Trim(root);
            return node?.ToJsonString(IrOutputOptions) ?? "{}";
        }

        /// <summary>Removes members that carry no information beyond the model's own defaults.</summary>
        private static void Trim(JsonObject obj)
        {
            // Depth first, so an object emptied by trimming its own members is then removed itself.
            foreach (var child in obj.ToList())
            {
                switch (child.Value)
                {
                    case JsonObject nested:
                        Trim(nested);
                        break;
                    case JsonArray array:
                        foreach (var item in array.OfType<JsonObject>()) Trim(item);
                        break;
                }
            }

            var partitioned = obj.TryGetPropertyValue("partitioned", out var p)
                && p is JsonValue pv && pv.TryGetValue<bool>(out var isPartitioned) && isPartitioned;

            foreach (var member in obj.ToList())
            {
                // The one numeric default worth dropping, and only where it cannot mean anything:
                // an archive threshold on an entity that is not partitioned is never read.
                if (!partitioned && member.Key == "archiveThresholdYears")
                {
                    obj.Remove(member.Key);
                    continue;
                }

                if (IsDefault(member.Value)) obj.Remove(member.Key);
            }
        }

        private static bool IsDefault(JsonNode? value) => value switch
        {
            null => true,
            JsonArray array => array.Count == 0,
            JsonObject nested => nested.Count == 0,
            JsonValue v when v.TryGetValue<bool>(out var b) => !b,
            JsonValue v when v.TryGetValue<string>(out var s) => string.IsNullOrEmpty(s),
            _ => false
        };

        private static string? Value(JsonObject obj, string name)
        {
            foreach (var member in obj)
            {
                if (member.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    return member.Value?.GetValue<string>();
                }
            }
            return null;
        }

        private static JsonObject? Nested(JsonObject obj, string name)
        {
            foreach (var member in obj)
            {
                if (member.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    return member.Value as JsonObject;
                }
            }
            return null;
        }

        private static T? Deserialize<T>(JsonObject source)
        {
            try
            {
                return source.Deserialize<T>(ReadOptions);
            }
            catch (JsonException)
            {
                // A node the canvas holds in a shape this cannot read is skipped rather than
                // failing the whole migration: the validator will report what is missing from the
                // result, which is a better message than a parse error deep inside a diagram.
                return default;
            }
        }

        private static List<T> DeserializeList<T>(JsonObject root, string name)
        {
            foreach (var member in root)
            {
                if (!member.Key.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
                if (member.Value is not JsonArray array) continue;

                var result = new List<T>();
                foreach (var item in array.OfType<JsonObject>())
                {
                    var model = Deserialize<T>(item);
                    if (model is not null) result.Add(model);
                }
                return result;
            }

            return [];
        }
    }
}
