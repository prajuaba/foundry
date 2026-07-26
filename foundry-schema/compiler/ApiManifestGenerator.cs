using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Foundry.Schema.Compiler
{
    /// <summary>
    /// Derives <c>api-manifest.json</c> from an IR document.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The REST surface is not emitted by this compiler. It is emitted by the
    /// <c>Foundry.Api.SourceGenerators</c> analyser, which reads <c>api-manifest.json</c> as an
    /// MSBuild <c>AdditionalFiles</c> item and generates <c>AddGeneratedHandlers()</c> and
    /// <c>MapGeneratedEndpoints()</c>. Without that file an application starts cleanly and serves
    /// no entity routes at all.
    /// </para>
    /// <para>
    /// Until now the only producer of that file was Studio's TypeScript
    /// (<c>store.ts: exportToApiManifest</c>), so a project scaffolded or compiled from the CLI had
    /// no manifest and therefore no CRUD — while <c>foundry new</c> reported a "READY-TO-RUN"
    /// project with "full REST CRUD". Deriving it here makes the CLI path produce a servable
    /// application, and makes the IR the single source for the API surface.
    /// </para>
    /// <para>
    /// Emitted as JSON text rather than as <c>Foundry.Api.Manifest.ApiManifest</c> instances,
    /// because the compiler deliberately does not reference the runtime assemblies.
    /// </para>
    /// </remarks>
    public static class ApiManifestGenerator
    {
        /// <summary>
        /// HTTP methods the endpoint generator understands for a generated CRUD surface.
        /// </summary>
        private static readonly HashSet<string> KnownMethods =
            new(StringComparer.OrdinalIgnoreCase) { "GET", "GET_BY_ID", "POST", "PUT", "DELETE" };

        /// <summary>
        /// Builds the manifest JSON for <paramref name="schema"/>.
        /// </summary>
        /// <param name="schema">A validated IR document.</param>
        /// <returns>Indented JSON suitable for writing to <c>api-manifest.json</c>.</returns>
        public static string Generate(SchemaModel schema)
        {
            if (schema is null) throw new ArgumentNullException(nameof(schema));

            var endpoints = new JsonArray();

            foreach (var entity in schema.Entities ?? new List<Entity>())
            {
                // No methods declared means no REST surface was asked for. That is a legitimate
                // choice (an entity may exist only as a workflow target or a DTO source), so it is
                // skipped rather than defaulted to full CRUD.
                var methods = (entity.ApiEnabledMethods ?? new List<string>())
                    .Where(m => !string.IsNullOrWhiteSpace(m))
                    .Select(m => m.Trim().ToUpperInvariant())
                    .Where(KnownMethods.Contains)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                if (methods.Count == 0) continue;

                var endpoint = new JsonObject
                {
                    ["Entity"] = entity.Name,
                    ["Route"] = RouteFor(entity.Name),
                    ["Methods"] = new JsonArray(methods.Select(m => (JsonNode)m!).ToArray())
                };

                AddMap(endpoint, "Roles", entity.ApiRoles, methods);
                AddBusinessRules(endpoint, entity.ApiBusinessRules, methods);
                AddCaching(endpoint, entity.ApiCaching, methods);

                endpoints.Add(endpoint);
            }

            var customEndpoints = new JsonArray();
            foreach (var custom in schema.CustomEndpoints ?? new List<CustomEndpoint>())
            {
                if (string.IsNullOrWhiteSpace(custom.Route)) continue;

                customEndpoints.Add(new JsonObject
                {
                    ["Route"] = custom.Route,
                    ["Method"] = (custom.Method ?? "GET").ToUpperInvariant(),
                    ["RequestType"] = custom.RequestType ?? string.Empty,
                    ["Roles"] = new JsonArray(),
                    ["BusinessRules"] = new JsonArray()
                });
            }

            var manifest = new JsonObject
            {
                ["Namespace"] = schema.Namespace ?? string.Empty,
                ["Endpoints"] = endpoints,
                ["CustomEndpoints"] = customEndpoints
            };

            return manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }

        /// <summary>
        /// Copies a per-method map, keeping only entries for methods actually exposed.
        /// </summary>
        private static void AddMap(
            JsonObject endpoint,
            string name,
            Dictionary<string, List<string>>? source,
            List<string> methods)
        {
            var result = new JsonObject();

            foreach (var pair in source ?? new Dictionary<string, List<string>>())
            {
                var method = pair.Key?.Trim().ToUpperInvariant();
                if (method is null || !methods.Contains(method, StringComparer.Ordinal)) continue;

                var values = (pair.Value ?? new List<string>())
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .Select(v => (JsonNode)v!)
                    .ToArray();

                if (values.Length > 0) result[method] = new JsonArray(values);
            }

            endpoint[name] = result;
        }

        private static void AddBusinessRules(
            JsonObject endpoint,
            Dictionary<string, List<string>>? source,
            List<string> methods)
            => AddMap(endpoint, "BusinessRules", source, methods);

        private static void AddCaching(
            JsonObject endpoint,
            Dictionary<string, ApiCachingConfig>? source,
            List<string> methods)
        {
            var result = new JsonObject();

            foreach (var pair in source ?? new Dictionary<string, ApiCachingConfig>())
            {
                var method = pair.Key?.Trim().ToUpperInvariant();
                if (method is null || !methods.Contains(method, StringComparer.Ordinal)) continue;
                if (pair.Value is null) continue;

                result[method] = new JsonObject
                {
                    ["Enabled"] = pair.Value.Enabled,
                    ["TtlSeconds"] = pair.Value.TtlSeconds
                };
            }

            endpoint["Caching"] = result;
        }

        /// <summary>
        /// Derives the collection route for an entity, e.g. <c>Category</c> to
        /// <c>/api/categories</c>.
        /// </summary>
        /// <remarks>
        /// The IR carries no per-entity CRUD route, so this must be deterministic: the same entity
        /// name always yields the same route, or a regenerated manifest would silently move a
        /// published endpoint.
        /// </remarks>
        internal static string RouteFor(string entityName)
        {
            if (string.IsNullOrWhiteSpace(entityName)) return "/api";
            return "/api/" + Pluralize(entityName).ToLowerInvariant();
        }

        /// <summary>
        /// Minimal English pluraliser, sufficient for route naming.
        /// </summary>
        internal static string Pluralize(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;

            // "Category" -> "Categories", but "Day" -> "Days".
            if (name.Length > 1
                && name.EndsWith("y", StringComparison.OrdinalIgnoreCase)
                && !"aeiou".Contains(char.ToLowerInvariant(name[name.Length - 2])))
            {
                return name.Substring(0, name.Length - 1) + "ies";
            }

            if (name.EndsWith("s", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("x", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("z", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("ch", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("sh", StringComparison.OrdinalIgnoreCase))
            {
                return name + "es";
            }

            return name + "s";
        }
    }
}
