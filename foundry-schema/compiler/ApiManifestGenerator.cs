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
                var methods = EnabledMethods(entity);

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

                // Emitted only when opted in, so an existing manifest without the key keeps its
                // meaning; the runtime treats a missing key as false, which is the declaration.
                if (entity.GraphQlEnabled) endpoint["GraphQL"] = true;

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

            var workflows = new JsonArray();
            foreach (var workflow in schema.Workflows ?? new List<WorkflowModel>())
            {
                if (string.IsNullOrWhiteSpace(workflow.Entity)) continue;

                workflows.Add(BuildWorkflow(workflow));

                // Each transition also becomes a route, so a workflow declared in a schema is
                // reachable over HTTP. Without this the definitions arrive, the pipeline behaviour is
                // registered, the commands are generated -- and nothing can ever send one.
                foreach (var endpoint in BuildTransitionEndpoints(workflow))
                {
                    customEndpoints.Add(endpoint);
                }
            }

            var manifest = new JsonObject
            {
                ["Namespace"] = schema.Namespace ?? string.Empty,
                ["Endpoints"] = endpoints,
                ["CustomEndpoints"] = customEndpoints,
                ["Workflows"] = workflows
            };

            return manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }

        /// <summary>
        /// Copies a workflow definition into the manifest shape the runtime reads.
        /// </summary>
        /// <remarks>
        /// The manifest is the only channel between the compiler and the running application, and
        /// <c>Workflows</c> was never written to it — so <c>ApiManifestWorkflowDefinitionProvider</c>
        /// always found an empty list, and a workflow declared in a schema was compiled, validated
        /// and then went nowhere. Every layer downstream was in place and waiting for a definition
        /// that never arrived.
        /// </remarks>
        private static JsonObject BuildWorkflow(WorkflowModel workflow)
        {
            var states = new JsonArray();
            foreach (var state in workflow.States ?? new List<WorkflowStateModel>())
            {
                if (string.IsNullOrWhiteSpace(state.Name)) continue;

                states.Add(new JsonObject
                {
                    ["Name"] = state.Name,
                    ["IsInitial"] = state.IsInitial,
                    ["IsFinal"] = state.IsFinal,
                    ["AllowedRoles"] = ToArray(state.AllowedRoles)
                });
            }

            var transitions = new JsonArray();
            foreach (var transition in workflow.Transitions ?? new List<WorkflowTransitionModel>())
            {
                if (string.IsNullOrWhiteSpace(transition.Trigger)) continue;

                var conditions = new JsonArray();
                foreach (var condition in transition.Conditions ?? new List<WorkflowConditionModel>())
                {
                    if (string.IsNullOrWhiteSpace(condition.Property)) continue;

                    conditions.Add(new JsonObject
                    {
                        ["Property"] = condition.Property,
                        ["Operator"] = condition.Operator ?? string.Empty,
                        ["Value"] = condition.Value ?? string.Empty
                    });
                }

                transitions.Add(new JsonObject
                {
                    // The engine matches on Id, so a transition that declares none is keyed by its
                    // trigger. Leaving it empty would make every such transition match the first one.
                    ["Id"] = TransitionId(transition),
                    ["Name"] = transition.Name ?? transition.Trigger,
                    ["FromState"] = transition.FromState ?? string.Empty,
                    ["ToState"] = transition.ToState ?? string.Empty,
                    ["Trigger"] = transition.Trigger,
                    ["RequiredRoles"] = ToArray(transition.RequiredRoles),
                    ["Conditions"] = conditions
                });
            }

            // Decision gates. The IR carries them, WorkflowConfig has a property for them and the
            // behaviour resolves them -- so omitting them here would leave a declared gate silently
            // absent at runtime, with its transition landing on whatever target state it named. That
            // is the same defect as the workflow list itself, one level down.
            var choiceNodes = new JsonArray();
            foreach (var node in workflow.ChoiceNodes ?? new List<WorkflowChoiceNodeModel>())
            {
                if (string.IsNullOrWhiteSpace(node.Id)) continue;

                var branches = new JsonArray();
                foreach (var branch in node.Branches ?? new List<WorkflowBranchModel>())
                {
                    if (string.IsNullOrWhiteSpace(branch.TargetState)) continue;

                    var branchConditions = new JsonArray();
                    if (!string.IsNullOrWhiteSpace(branch.Condition?.Property))
                    {
                        branchConditions.Add(new JsonObject
                        {
                            ["Property"] = branch.Condition!.Property,
                            ["Operator"] = branch.Condition.Operator ?? string.Empty,
                            ["Value"] = branch.Condition.Value ?? string.Empty
                        });
                    }

                    branches.Add(new JsonObject
                    {
                        ["ToState"] = branch.TargetState,
                        ["Conditions"] = branchConditions
                    });
                }

                choiceNodes.Add(new JsonObject
                {
                    ["Id"] = node.Id,
                    ["Name"] = node.Name ?? node.Id,

                    // Carried from the IR rather than hardcoded empty. It was hardcoded, and the
                    // engine assigned it regardless, so an unmatched gate put the record into the
                    // empty state and saved it. Absent here still means "refuse", which is what the
                    // comment this replaces claimed was happening.
                    ["DefaultState"] = node.DefaultState ?? string.Empty,
                    ["Branches"] = branches
                });
            }

            return new JsonObject
            {
                ["Id"] = string.IsNullOrWhiteSpace(workflow.Id) ? workflow.Name : workflow.Id,
                ["Name"] = workflow.Name ?? string.Empty,
                ["Entity"] = workflow.Entity,
                ["Version"] = workflow.Version ?? string.Empty,
                ["EffectiveDate"] = workflow.EffectiveDate ?? string.Empty,
                ["ExpirationDate"] = workflow.ExpirationDate ?? string.Empty,

                // A definition the IR did not mark active is still emitted, but the engine skips it.
                // Emitting it keeps the manifest a faithful record of the schema.
                ["IsActive"] = workflow.IsActive,
                ["States"] = states,
                ["Transitions"] = transitions,
                ["ChoiceNodes"] = choiceNodes
            };
        }

        /// <summary>
        /// Emits one endpoint per transition, so a transition can actually be triggered.
        /// </summary>
        /// <remarks>
        /// Deliberately expressed as custom endpoints rather than as a new kind of route. The
        /// generated transition command already implements <c>IRequest</c>, the endpoint generator
        /// already maps a custom endpoint by POSTing its request type, and the workflow behaviour
        /// already intercepts anything implementing <c>IWorkflowTransitionRequest</c> — so the whole
        /// path exists and only needed connecting. Roles declared on the transition are carried onto
        /// the endpoint, where they are enforced before the request reaches the pipeline.
        /// </remarks>
        private static IEnumerable<JsonObject> BuildTransitionEndpoints(WorkflowModel workflow)
        {
            foreach (var transition in workflow.Transitions ?? new List<WorkflowTransitionModel>())
            {
                if (string.IsNullOrWhiteSpace(transition.Trigger)) continue;

                yield return new JsonObject
                {
                    ["Route"] = TransitionRouteFor(workflow.Entity, transition.Trigger),
                    ["Method"] = "POST",

                    // The command is emitted into the '<namespace>.Commands' namespace, and the
                    // endpoint generator qualifies the request type with the manifest namespace.
                    ["RequestType"] = "Commands." + transition.Trigger,
                    ["Roles"] = ToArray(transition.RequiredRoles),
                    ["BusinessRules"] = new JsonArray()
                };
            }
        }

        /// <summary>Route for triggering one transition, e.g. <c>/api/orders/transitions/approve</c>.</summary>
        /// <remarks>Public for the same reason as <see cref="RouteFor"/>: so nobody recomputes it.</remarks>
        public static string TransitionRouteFor(string entityName, string trigger)
            => RouteFor(entityName) + "/transitions/" + trigger.ToLowerInvariant();

        /// <summary>The identifier the workflow engine matches a transition on.</summary>
        internal static string TransitionId(WorkflowTransitionModel transition)
            => string.IsNullOrWhiteSpace(transition.Id) ? transition.Trigger : transition.Id;

        private static JsonArray ToArray(IEnumerable<string>? values)
            => new((values ?? Enumerable.Empty<string>())
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => (JsonNode)v!)
                .ToArray());

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
        /// <summary>
        /// The HTTP methods an entity actually exposes.
        /// </summary>
        /// <remarks>
        /// No methods declared means no REST surface was asked for. That is a legitimate choice (an
        /// entity may exist only as a workflow target or a DTO source), so it is skipped rather than
        /// defaulted to full CRUD.
        ///
        /// Shared with the exporters deliberately. They each decided for themselves what an entity
        /// published — every one of them assuming full CRUD — so the OpenAPI and Postman documents
        /// described endpoints that returned 404, for entities that had no REST surface at all.
        /// </remarks>
        /// <summary>
        /// The HTTP methods an entity actually exposes.
        /// </summary>
        /// <remarks>
        /// Public because other Foundry tools need the answer and, when they could not ask for it,
        /// they guessed. Studio gave an entity with no declared methods a full CRUD surface; the
        /// autonomous test generator emitted REST suites for entities that expose nothing at all.
        /// A rule kept private is a rule that gets reimplemented.
        /// </remarks>
        public static List<string> EnabledMethods(Entity entity)
            => (entity.ApiEnabledMethods ?? new List<string>())
                .Where(m => !string.IsNullOrWhiteSpace(m))
                .Select(m => m.Trim().ToUpperInvariant())
                .Where(KnownMethods.Contains)
                .Distinct(StringComparer.Ordinal)
                .ToList();

        /// <summary>
        /// The route the generated API serves for an entity.
        /// </summary>
        /// <remarks>
        /// Public for the same reason. This exact rule has been reimplemented wrongly four times —
        /// the OpenAPI exporter, the Postman exporter, Studio and the test generator all composed
        /// <c>/api/v1/{lowercase-singular}</c> while the application serves <c>/api/{plural}</c>.
        /// </remarks>
        public static string RouteFor(string entityName)
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
