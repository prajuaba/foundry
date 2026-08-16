using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Foundry.Schema.Compiler.Exporters;

/// <summary>
/// Gap record representing a topic that cannot be derived from the schema.
/// </summary>
public record Gap(
    string Key,
    int? Section,
    string Topic,
    string Why,
    string Detail);

/// <summary>
/// Coverage claim: something the generator actually emitted.
/// </summary>
public record Claim(string Key, string Text);

/// <summary>
/// Tracks coverage claims as emitters run.
/// </summary>
public class Coverage
{
    private readonly Dictionary<string, string> _claims = new();

    /// <summary>
    /// Record a coverage claim only when substantive content was emitted.
    /// </summary>
    public void Claim(string key, string text)
    {
        if (!_claims.ContainsKey(key))
        {
            _claims[key] = text;
        }
    }

    /// <summary>
    /// Return claims in insertion order.
    /// </summary>
    public IReadOnlyList<Claim> Claims => _claims.Select(kv => new Claim(kv.Key, kv.Value)).ToList().AsReadOnly();
}

/// <summary>
/// Represents a divergence between IR and manifest.
/// </summary>
public record Divergence(
    string Kind,
    string Element,
    string IrValue,
    string ManifestValue,
    string Consequence);

/// <summary>
/// Compares IR and manifest for divergences.
/// </summary>
public class DivergenceCheck
{
    private readonly List<Divergence> _divergences = new();

    /// <summary>
    /// The single definition: business rules are enforced via dependency injection, not manifest declarations.
    /// Referenced by the reference exporter's markdown table and the verify command's console output.
    /// This is the only place readers are told that a rule missing from the manifest is still enforced via DI.
    ///
    /// Must not be duplicated. Previously existed in three drifting copies, risking that documentation
    /// inconsistencies would be mistaken for enforcement gaps. This constant documents the defect in the code that fixed it.
    /// </summary>
    public const string DocumentationInconsistencyNote = "Business rules are enforced via DI, not the manifest. This is descriptive inconsistency, not an enforcement gap.";

    public IReadOnlyList<Divergence> Divergences => _divergences.AsReadOnly();
    public int EntitiesChecked { get; private set; }
    public int CustomEndpointsChecked { get; private set; }
    public int TransitionsDerived { get; private set; }

    public void AddDivergence(string kind, string element, string irValue, string manifestValue, string consequence)
    {
        _divergences.Add(new Divergence(kind, element, irValue, manifestValue, consequence));
    }

    /// <summary>
    /// Normalize roles to uppercase methods and sorted role lists.
    /// Why overloads exist: The IR model provides strongly-typed Dictionary&lt;string, List&lt;string&gt;&gt;,
    /// but parsed JSON yields Dictionary&lt;string, object&gt; with List&lt;object&gt; values. C# generics are
    /// invariant, so Dictionary&lt;string, List&lt;string&gt;&gt; does NOT match `is Dictionary&lt;string, object&gt;`.
    /// A single object-typed helper silently matched neither. Separate overloads handle each path correctly.
    /// </summary>
    public Dictionary<string, List<string>> NormalizeRoles(Dictionary<string, List<string>>? irRoles)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        if (irRoles != null)
        {
            foreach (var kvp in irRoles)
            {
                if (kvp.Value?.Count > 0)
                {
                    result[kvp.Key.ToUpper()] = new List<string>(kvp.Value).OrderBy(r => r).ToList();
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Normalize roles from JSON-parsed manifest (Dictionary&lt;string, object&gt; with List&lt;object&gt; values).
    /// </summary>
    public Dictionary<string, List<string>> NormalizeRoles(Dictionary<string, object>? manifestRoles)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        if (manifestRoles != null)
        {
            foreach (var kvp in manifestRoles)
            {
                if (kvp.Value is List<object> roleList && roleList.Count > 0)
                {
                    var stringRoles = roleList.Select(o => o?.ToString() ?? "").Where(s => !string.IsNullOrEmpty(s)).ToList();
                    if (stringRoles.Count > 0)
                    {
                        result[kvp.Key.ToUpper()] = stringRoles.OrderBy(r => r).ToList();
                    }
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Normalize business rules from IR model (strongly-typed Dictionary&lt;string, List&lt;string&gt;&gt;).
    /// Why overloads exist: same reason as NormalizeRoles — IR provides strongly-typed input,
    /// manifest provides JSON-parsed input with different shape. Separate overloads handle each correctly.
    /// </summary>
    public Dictionary<string, List<string>> NormalizeRules(Dictionary<string, List<string>>? irRules)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        if (irRules != null)
        {
            foreach (var kvp in irRules)
            {
                if (kvp.Value?.Count > 0)
                {
                    result[kvp.Key.ToUpper()] = new List<string>(kvp.Value).OrderBy(r => r).ToList();
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Normalize business rules from JSON-parsed manifest (Dictionary&lt;string, object&gt; with List&lt;object&gt; values).
    /// </summary>
    public Dictionary<string, List<string>> NormalizeRules(Dictionary<string, object>? manifestRules)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        if (manifestRules != null)
        {
            foreach (var kvp in manifestRules)
            {
                if (kvp.Value is List<object> ruleList && ruleList.Count > 0)
                {
                    var stringRules = ruleList.Select(o => o?.ToString() ?? "").Where(s => !string.IsNullOrEmpty(s)).ToList();
                    if (stringRules.Count > 0)
                    {
                        result[kvp.Key.ToUpper()] = stringRules.OrderBy(r => r).ToList();
                    }
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Return true if roles are identical (ignoring order).
    /// Roles are sorted for comparison because roles reach AuthorizeAttribute.Roles via string.Join(",", roles)
    /// and ASP.NET treats that as a set. Order carries no meaning at enforcement point.
    /// </summary>
    public bool CompareRoleLists(List<string> irRoles, List<string> manifestRoles)
    {
        return new HashSet<string>(irRoles).SetEquals(manifestRoles);
    }

    /// <summary>
    /// Run the divergence check.
    /// </summary>
    public void Run(SchemaModel ir, Dictionary<string, object>? manifest)
    {
        var entities = ir.Entities.ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);
        var irCustomEndpoints = new Dictionary<(string route, string method), CustomEndpoint>(new RouteMethodEqualityComparer());

        foreach (var ep in ir.CustomEndpoints ?? Enumerable.Empty<CustomEndpoint>())
        {
            irCustomEndpoints[(ep.Route, ep.Method.ToUpperInvariant())] = ep;
        }

        // Get manifest endpoints
        var manifestEndpoints = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
        var manifestCustomEndpoints = new List<Dictionary<string, object>>();

        if (manifest != null && manifest.TryGetValue("Endpoints", out var endPointsObj) && endPointsObj is List<object> endPointsList)
        {
            foreach (var ep in endPointsList)
            {
                if (ep is Dictionary<string, object> epDict)
                {
                    if (epDict.TryGetValue("Entity", out var entityValue) && entityValue is string entityName)
                    {
                        manifestEndpoints[entityName] = epDict;
                    }
                }
            }
        }

        if (manifest != null && manifest.TryGetValue("CustomEndpoints", out var customEndPointsObj) && customEndPointsObj is List<object> customEndPointsList)
        {
            foreach (var ep in customEndPointsList)
            {
                if (ep is Dictionary<string, object> epDict)
                {
                    manifestCustomEndpoints.Add(epDict);
                }
            }
        }

        // Count transition-derived endpoints
        var transitionDerived = new List<Dictionary<string, object>>();
        foreach (var ep in manifestCustomEndpoints)
        {
            if (ep.TryGetValue("RequestType", out var requestTypeObj) &&
                requestTypeObj is string requestType &&
                !string.IsNullOrEmpty(requestType) &&
                requestType.StartsWith("Commands."))
            {
                transitionDerived.Add(ep);
            }
        }
        TransitionsDerived = transitionDerived.Count;

        // Check for presence mismatches: entities in IR but not in manifest
        var irEntityNames = new HashSet<string>(entities.Keys, StringComparer.OrdinalIgnoreCase);
        var manifestEntityNames = new HashSet<string>(manifestEndpoints.Keys, StringComparer.OrdinalIgnoreCase);
        var irOnlyEntities = irEntityNames.Except(manifestEntityNames).ToList();

        foreach (var entityName in irOnlyEntities.OrderBy(e => e))
        {
            var entity = entities[entityName];
            var enabledMethods = string.Join(", ", entity.ApiEnabledMethods ?? Enumerable.Empty<string>());
            AddDivergence(
                "enforcement_gap",
                $"Entity {entityName} CRUD",
                $"declared with {enabledMethods}",
                "not enforced",
                "Entity is declared in IR but not enforced.");
        }

        // Check entity CRUD methods and roles
        foreach (var manifestEp in manifestEndpoints)
        {
            EntitiesChecked++;
            var entityName = manifestEp.Key;

            if (!entities.TryGetValue(entityName, out var irEntity))
            {
                AddDivergence(
                    "enforcement_gap",
                    $"Entity {entityName} CRUD",
                    "not declared",
                    string.Join(", ", (manifestEp.Value.GetValueOrDefault("Methods") as List<object>)?.Select(o => o.ToString() ?? "") ?? Enumerable.Empty<string>()),
                    "Entity is enforced but not declared in IR.");
                continue;
            }

            var irMethods = new HashSet<string>(irEntity.ApiEnabledMethods ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            var manifestMethods = new HashSet<string>((manifestEp.Value.GetValueOrDefault("Methods") as List<object>)?.Select(o => o.ToString() ?? "").ToList() ?? new List<string>(), StringComparer.OrdinalIgnoreCase);

            if (!irMethods.SetEquals(manifestMethods))
            {
                AddDivergence(
                    "enforcement_gap",
                    $"Entity {entityName} methods",
                    string.Join(", ", irMethods.OrderBy(m => m)),
                    string.Join(", ", manifestMethods.OrderBy(m => m)),
                    "Enforced methods differ from declared methods.");
            }

            // Check roles
            var irRoles = NormalizeRoles(irEntity.ApiRoles);
            var manifestRolesObj = manifestEp.Value.GetValueOrDefault("Roles");
            Dictionary<string, List<string>> manifestRolesDict = new();

            if (manifestRolesObj is Dictionary<string, object> mRoles)
            {
                foreach (var kvp in mRoles)
                {
                    if (kvp.Key.ToString() is string key && kvp.Value is List<object> roleList)
                    {
                        manifestRolesDict[key.ToUpper()] = roleList.Select(o => o?.ToString() ?? "").Where(s => !string.IsNullOrEmpty(s)).OrderBy(r => r).ToList();
                    }
                }
            }

            var allMethodsInRoles = new HashSet<string>(irRoles.Keys.Union(manifestRolesDict.Keys));

            foreach (var method in allMethodsInRoles.OrderBy(m => m))
            {
                irRoles.TryGetValue(method, out var irRoleList);
                manifestRolesDict.TryGetValue(method, out var manifestRoleList);

                if (!CompareRoleLists(irRoleList ?? new List<string>(), manifestRoleList ?? new List<string>()))
                {
                    string consequence;
                    if (manifestRoleList == null || manifestRoleList.Count == 0)
                    {
                        consequence = "Endpoint is served to any authenticated caller; declared roles are not enforced.";
                    }
                    else if (irRoleList != null && irRoleList.Count > 0)
                    {
                        var declaredOnly = new HashSet<string>(irRoleList).Except(manifestRoleList ?? Enumerable.Empty<string>()).ToList();
                        if (declaredOnly.Count > 0)
                        {
                            consequence = $"Callers with only roles {string.Join(", ", declaredOnly.OrderBy(r => r))} are affected.";
                        }
                        else
                        {
                            consequence = string.Empty;
                        }
                    }
                    else
                    {
                        consequence = "Roles enforced for " + method + " differ from declaration.";
                    }

                    if (!string.IsNullOrEmpty(consequence))
                    {
                        AddDivergence(
                            "enforcement_gap",
                            $"Entity {entityName} {method}",
                            irRoleList != null ? string.Join(", ", irRoleList) : "(no roles)",
                            manifestRoleList != null ? string.Join(", ", manifestRoleList) : "(no roles)",
                            consequence);
                    }
                }
            }

            // Check business rules
            var irRulesDict = NormalizeRules(irEntity.ApiBusinessRules);
            var manifestRulesObj = manifestEp.Value.GetValueOrDefault("BusinessRules");

            Dictionary<string, List<string>> manifestRulesDict = new();
            if (manifestRulesObj is Dictionary<string, object> mRules)
            {
                foreach (var kvp in mRules)
                {
                    if (kvp.Key.ToString() is string key && kvp.Value is List<object> ruleList)
                    {
                        manifestRulesDict[key.ToUpper()] = ruleList.Select(o => o?.ToString() ?? "").Where(s => !string.IsNullOrEmpty(s)).OrderBy(r => r).ToList();
                    }
                }
            }

            var allMethodsInRules = new HashSet<string>(irRulesDict.Keys.Union(manifestRulesDict.Keys));

            foreach (var method in allMethodsInRules.OrderBy(m => m))
            {
                irRulesDict.TryGetValue(method, out var irRuleList);
                manifestRulesDict.TryGetValue(method, out var manifestRuleList);

                if (!CompareRoleLists(irRuleList ?? new List<string>(), manifestRuleList ?? new List<string>()))
                {
                    AddDivergence(
                        "documentation_inconsistency",
                        $"Entity {entityName} {method} rules",
                        irRuleList != null ? string.Join(", ", irRuleList) : "(none)",
                        manifestRuleList != null ? string.Join(", ", manifestRuleList) : "(none)",
                        "Business rule documentation differs.");
                }
            }
        }

        // Check custom endpoints (excluding transition-derived ones)
        foreach (var ep in manifestCustomEndpoints)
        {
            if (ep.TryGetValue("RequestType", out var requestTypeObj) &&
                requestTypeObj is string requestType &&
                !string.IsNullOrEmpty(requestType) &&
                requestType.StartsWith("Commands."))
            {
                continue; // Skip transition-derived endpoints
            }

            CustomEndpointsChecked++;

            string route = ep.GetValueOrDefault("Route")?.ToString() ?? "";
            string method = (ep.GetValueOrDefault("Method") as string)?.ToUpperInvariant() ?? "";

            var key = (route, method);
            if (!irCustomEndpoints.ContainsKey(key))
            {
                AddDivergence(
                    "enforcement_gap",
                    $"Custom endpoint {method} {route}",
                    "not declared",
                    $"declared with {ep.GetValueOrDefault("RequestType", "?")}",
                    "Endpoint is enforced but not declared in IR.");
                continue;
            }

            var irEp = irCustomEndpoints[key];

            // Check roles
            var irRolesList = new List<string>(irEp.Roles ?? Enumerable.Empty<string>());
            var manifestRolesObj = ep.GetValueOrDefault("Roles");
            var manifestRolesList = new List<string>();

            if (manifestRolesObj is List<object> mRoleList)
            {
                manifestRolesList = mRoleList.Select(o => o?.ToString() ?? "").Where(s => !string.IsNullOrEmpty(s)).ToList();
            }

            if (!CompareRoleLists(irRolesList.OrderBy(r => r).ToList(), manifestRolesList.OrderBy(r => r).ToList()))
            {
                string consequence;
                if (manifestRolesList.Count == 0 && irRolesList.Count > 0)
                {
                    consequence = "Endpoint is served to any authenticated caller; declared roles are not enforced.";
                }
                else if (irRolesList.Count > 0 && manifestRolesList.Count > 0)
                {
                    var declaredOnly = new HashSet<string>(irRolesList).Except(manifestRolesList).ToList();
                    if (declaredOnly.Count > 0)
                    {
                        consequence = $"Callers with only roles {string.Join(", ", declaredOnly.OrderBy(r => r))} are affected.";
                    }
                    else
                    {
                        consequence = string.Empty;
                    }
                }
                else
                {
                    consequence = "Roles enforced differ from declaration.";
                }

                if (!string.IsNullOrEmpty(consequence))
                {
                    AddDivergence(
                        "enforcement_gap",
                        $"Custom endpoint {method} {route}",
                        irRolesList.Count > 0 ? string.Join(", ", irRolesList) : "(no roles)",
                        manifestRolesList.Count > 0 ? string.Join(", ", manifestRolesList) : "(no roles)",
                        consequence);
                }
            }

            // Check business rules
            var irRulesList = new List<string>(irEp.BusinessRules ?? Enumerable.Empty<string>());
            var manifestRulesObj2 = ep.GetValueOrDefault("BusinessRules");
            var manifestRulesList = new List<string>();

            if (manifestRulesObj2 is List<object> mRuleList)
            {
                manifestRulesList = mRuleList.Select(o => o?.ToString() ?? "").Where(s => !string.IsNullOrEmpty(s)).ToList();
            }

            if (!CompareRoleLists(irRulesList.OrderBy(r => r).ToList(), manifestRulesList.OrderBy(r => r).ToList()))
            {
                AddDivergence(
                    "documentation_inconsistency",
                    $"Custom endpoint {method} {route} rules",
                    irRulesList.Count > 0 ? string.Join(", ", irRulesList) : "(none)",
                    manifestRulesList.Count > 0 ? string.Join(", ", manifestRulesList) : "(none)",
                    "Business rule documentation differs.");
            }
        }

        // Check for IR custom endpoints not in manifest (excluding those that might be transition-derived)
        foreach (var kvp in irCustomEndpoints)
        {
            var (route, method) = kvp.Key;
            var irEp = kvp.Value;

            bool found = false;
            foreach (var ep in manifestCustomEndpoints)
            {
                if (ep.TryGetValue("RequestType", out var requestTypeObj3) &&
                    requestTypeObj3 is string requestType3 &&
                    !string.IsNullOrEmpty(requestType3) &&
                    requestType3.StartsWith("Commands."))
                {
                    continue; // Skip transition-derived endpoints
                }

                string epRoute = ep.GetValueOrDefault("Route")?.ToString() ?? "";
                string epMethod = (ep.GetValueOrDefault("Method") as string)?.ToUpperInvariant() ?? "";

                if (epRoute == route && epMethod == method)
                {
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                AddDivergence(
                    "enforcement_gap",
                    $"Custom endpoint {method} {route}",
                    $"declared with {irEp.RequestType ?? "?"}",
                    "not enforced",
                    "Endpoint is declared in IR but not enforced.");
            }
        }
    }

    private class RouteMethodEqualityComparer : IEqualityComparer<(string route, string method)>
    {
        public bool Equals((string route, string method) x, (string route, string method) y)
        {
            return x.route == y.route && x.method == y.method;
        }

        public int GetHashCode((string route, string method) obj)
        {
            return HashCode.Combine(obj.route, obj.method.ToUpperInvariant());
        }
    }
}

public class ReferenceExportException : Exception
{
    public ReferenceExportException(string message) : base(message) { }
}

/// <summary>
/// Static GAPS registry - exactly 8 entries from Python source.
/// </summary>
public static class GapsRegistry
{
    public static readonly List<Gap> Gaps = new()
    {
        new Gap("rationale", null, "Rationale",
            "Architectural decisions and design philosophy are not expressed in the entity model.",
            "Architectural decisions and design philosophy."),
        new Gap("non_functional_requirements", null, "Non-functional Requirements",
            "Performance targets, availability SLOs, and capacity thresholds are not schema-encoded.",
            "Performance targets, availability SLOs, and capacity thresholds."),
        new Gap("deployment_topology", null, "Deployment Topology",
            "Infrastructure, regions, and scaling strategy are external to the schema.",
            "Infrastructure, regions, and scaling strategy."),
        new Gap("runbooks_and_dr", null, "Runbooks and Disaster Recovery",
            "Operational procedures and DR plans cannot be inferred from data structures.",
            "Operational procedures and DR plans."),
        new Gap("custom_endpoint_request_bodies", 7, "Custom Endpoint Request Bodies",
            "JSON/XML structure and field semantics of request payloads are not in the IR.",
            "JSON/XML structure and field semantics of request payloads."),
        new Gap("business_rule_bodies", 11, "Business Rule Bodies",
            "Validation logic is hand-authored and not derivable from rule names alone.",
            "Validation logic for rule implementations."),
        new Gap("event_contracts", 8, "Event Contracts",
            "Message field names, types, and consumer implementations are not in the schema.",
            "Message field names, types, and consumer implementations."),
        new Gap("api_routes", 7, "API Routes",
            "Route paths are assigned by the compiler; provide api-manifest.json for literal routes.",
            "Route paths are assigned by the compiler; provide api-manifest.json for literal routes.")
    };
}

/// <summary>
/// Exporter that generates a 12-section technical reference markdown document from a Foundry IR schema.
/// </summary>
public static class ReferenceExporter
{
    private static Dictionary<string, object>? ConvertJsonElementToDictionary(System.Text.Json.JsonElement element)
    {
        if (element.ValueKind != System.Text.Json.JsonValueKind.Object) return null;

        var dict = new Dictionary<string, object>();
        foreach (var prop in element.EnumerateObject())
        {
            dict[prop.Name] = ConvertJsonElement(prop.Value);
        }
        return dict;
    }

    private static object ConvertJsonElement(System.Text.Json.JsonElement element)
    {
        return element.ValueKind switch
        {
            System.Text.Json.JsonValueKind.Object => ConvertJsonElementToDictionary(element) ?? (object)new Dictionary<string, object>(),
            System.Text.Json.JsonValueKind.Array => element.EnumerateArray().Select(ConvertJsonElement).ToList(),
            System.Text.Json.JsonValueKind.String => element.GetString() ?? "",
            System.Text.Json.JsonValueKind.Number => element.GetDouble(),
            System.Text.Json.JsonValueKind.True => true,
            System.Text.Json.JsonValueKind.False => false,
            System.Text.Json.JsonValueKind.Null => "",
            _ => ""
        };
    }

    /// <summary>
    /// Converts a JsonNode to Dictionary&lt;string, object&gt; for use by DivergenceCheck and other consumers.
    /// </summary>
    public static Dictionary<string, object>? ConvertJsonNodeToDictionary(JsonNode? node)
    {
        if (node is null) return null;

        try
        {
            var element = JsonSerializer.SerializeToElement(node, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return ConvertJsonElementToDictionary(element);
        }
        catch
        {
            return null;
        }
    }

    private static string EscapePipe(string? s)
    {
        return s?.Replace("|", "\\|") ?? "";
    }

    private static List<string> EmitTitle(SchemaModel ir, string irFile, string irHash, string? manifestFile, string? manifestHash)
    {
        var lines = new List<string>();

        lines.Add($"# {ir.Namespace} — Technical Reference");
        lines.Add("");
        lines.Add("> **Auto-generated from IR schema. Do not hand-edit.**");

        var sourceLine = $"> Source: {Path.GetFileName(irFile)} | Version: {ir.Version} | SHA256: {irHash}";
        if (!string.IsNullOrEmpty(manifestFile) && !string.IsNullOrEmpty(manifestHash))
        {
            sourceLine += $"\n> Manifest: {Path.GetFileName(manifestFile)} | SHA256: {manifestHash}";
        }

        lines.Add(sourceLine);
        lines.Add("");

        return lines;
    }

    private static List<string> EmitScope(List<Gap> activeGaps, Coverage coverage)
    {
        var lines = new List<string>();
        lines.Add("## 1. Scope");
        lines.Add("");
        lines.Add("### Topics Covered");
        lines.Add("");
        lines.Add("This reference documents the data model, authorization rules, workflows, API surface, and external integrations derived from the Foundry IR schema. It is the authoritative source for:");
        lines.Add("");

        var coveredKeys = new HashSet<string>(coverage.Claims.Select(c => c.Key));

        // Static list of topics that should appear if covered
        var coverageTopics = new (string key, string text)[]
        {
            ("entities", "Entity definitions and relationships"),
            ("properties", "Property types, constraints, and protection levels"),
            ("rbac", "Role-based access control (CRUD and resource-level)"),
            ("workflows", "Workflow state machines and transitions"),
            ("custom_endpoints", "Custom endpoint routes, methods, roles, and filters"),
            ("persistence", "Persistence (indexes, caching, archival policies)"),
            ("events", "Real-time and event-driven capabilities"),
            ("integrations", "External system integrations")
        };

        foreach (var topic in coverageTopics)
        {
            if (coveredKeys.Contains(topic.key))
            {
                lines.Add($"- {topic.text}");
            }
        }

        lines.Add("");
        lines.Add("### Topics Not Covered");
        lines.Add("");
        lines.Add("The following require hand-authored documentation:");
        lines.Add("");
        lines.Add("| Topic | Why Not Derivable |");
        lines.Add("| --- | --- |");

        // Sort by key for deterministic output
        foreach (var gap in activeGaps.OrderBy(g => g.Key))
        {
            lines.Add($"| {gap.Topic} | {gap.Why} |");
        }

        lines.Add("");

        return lines;
    }

    private static List<string> EmitOverview(SchemaModel ir, Coverage coverage)
    {
        var lines = new List<string>();
        lines.Add("## 2. System Overview");
        lines.Add("");

        var entities = ir.Entities ?? new();
        var enums = ir.Enums ?? new();
        var dtos = ir.Dtos ?? new();
        var endpoints = ir.CustomEndpoints ?? new();
        var workflows = ir.Workflows ?? new();
        var connectors = ir.Connectors ?? new();

        int totalProperties = entities.Sum(e => e.Properties?.Count ?? 0);
        int totalIndexes = entities.Sum(e => e.Indexes?.Count ?? 0);

        var rolesSet = new HashSet<string>();
        foreach (var entity in entities)
        {
            if (entity.ApiRoles != null)
            {
                foreach (var roleList in entity.ApiRoles.Values)
                    rolesSet.UnionWith(roleList ?? new());
            }
        }
        foreach (var workflow in workflows)
        {
            foreach (var state in workflow.States ?? new())
                rolesSet.UnionWith(state.AllowedRoles ?? new());
            foreach (var transition in workflow.Transitions ?? new())
                rolesSet.UnionWith(transition.RequiredRoles ?? new());
        }
        foreach (var endpoint in endpoints)
            rolesSet.UnionWith(endpoint.Roles ?? new());

        var kafkaTopics = new HashSet<string>();
        foreach (var entity in entities)
            if (!string.IsNullOrEmpty(entity.KafkaTopic))
                kafkaTopics.Add(entity.KafkaTopic);
        foreach (var dto in dtos)
            if (!string.IsNullOrEmpty(dto.KafkaTopic))
                kafkaTopics.Add(dto.KafkaTopic);

        int realtimeCount = entities.Count(e => e.RealTime);

        var rulesSet = new HashSet<string>();
        foreach (var entity in entities)
        {
            if (entity.ApiBusinessRules != null)
            {
                foreach (var ruleList in entity.ApiBusinessRules.Values)
                    rulesSet.UnionWith(ruleList ?? new());
            }
        }
        foreach (var endpoint in endpoints)
            rulesSet.UnionWith(endpoint.BusinessRules ?? new());

        int endpointsWithAssignments = endpoints.Count(e => e.Assignments?.Count > 0);
        int endpointsWithRules = endpoints.Count(e => e.BusinessRules?.Count > 0);
        int cachingCount = entities.Sum(e => e.ApiCaching?.Count ?? 0);

        lines.Add("| Metric | Count |");
        lines.Add("| --- | --- |");
        lines.Add($"| Entities | {entities.Count} |");
        lines.Add($"| Enums | {enums.Count} |");
        lines.Add($"| DTOs | {dtos.Count} |");
        lines.Add($"| Custom Endpoints | {endpoints.Count} |");
        lines.Add($"| Workflows | {workflows.Count} |");
        lines.Add($"| Connectors | {connectors.Count} |");
        lines.Add($"| Total Properties | {totalProperties} |");
        lines.Add($"| Total Indexes | {totalIndexes} |");
        lines.Add($"| Caching Configurations | {cachingCount} |");
        lines.Add($"| Distinct Roles | {rolesSet.Count} |");
        lines.Add($"| Kafka Topics | {kafkaTopics.Count} |");
        lines.Add($"| Real-Time Entities | {realtimeCount} |");
        lines.Add($"| Distinct Business Rules | {rulesSet.Count} |");
        lines.Add($"| Endpoints with Assignments | {endpointsWithAssignments} |");
        lines.Add($"| Endpoints with Rules | {endpointsWithRules} |");
        lines.Add("");
        return lines;
    }

    private static List<string> EmitDomainModel(SchemaModel ir, Coverage coverage)
    {
        var lines = new List<string>();
        lines.Add("## 3. Domain Model");
        lines.Add("");

        var entities = ir.Entities ?? new();
        if (entities.Count > 0)
        {
            coverage.Claim("entities", "Entity definitions and relationships");
            if (entities.Any(e => (e.Properties?.Count ?? 0) > 0))
                coverage.Claim("properties", "Property types, constraints, and protection levels");
        }

        int idx = 1;
        foreach (var entity in entities)
        {
            lines.Add($"### 3.{idx} {entity.Name}");
            lines.Add("");

            var flags = new List<string>();
            if (entity.MultiTenant) flags.Add("multi-tenant");
            if (entity.SoftDelete) flags.Add("soft-delete");
            if (entity.Auditable) flags.Add("auditable");
            if (entity.GraphQlEnabled) flags.Add("GraphQL");
            if (entity.RealTime) flags.Add("real-time");
            if (entity.KafkaOutboxEnabled) flags.Add("Kafka outbox");
            if (entity.FileIoEnabled) flags.Add("file-IO");
            if (entity.OwnerScoped) flags.Add("owner-scoped");
            if (entity.Partitioned) flags.Add("partitioned");
            // Only show archive flag if explicitly set (by correlation with partitioned flag in the IR)
            if (entity.ArchiveThresholdYears > 0 && entity.Partitioned) flags.Add($"archive-after-{entity.ArchiveThresholdYears}-years");

            if (flags.Count > 0)
            {
                lines.Add($"**Flags**: {string.Join(", ", flags)}");
                lines.Add("");
            }

            lines.Add("| Property | Type | Constraints | Notes |");
            lines.Add("| --- | --- | --- | --- |");

            foreach (var prop in entity.Properties ?? new())
            {
                string propName = EscapePipe(prop.Name);
                string propType = EscapePipe(prop.Type);
                string constraints = (prop.Attributes?.Count ?? 0) > 0
                    ? string.Join(", ", prop.Attributes.Select(EscapePipe))
                    : "—";

                var notes = new List<string>();
                if (prop.IsKey) notes.Add("[Key]");
                if (prop.IsTenantKey) notes.Add("[Tenant Key]");
                if (prop.IsOwnerKey) notes.Add("[Owner Key]");
                if (prop.IsEnum) notes.Add("[Enum]");
                if (!string.IsNullOrEmpty(prop.SensitiveCategory))
                    notes.Add($"[Sensitive: {EscapePipe(prop.SensitiveCategory)}]");

                string notesStr = notes.Count > 0 ? string.Join(" ", notes) : "—";
                lines.Add($"| {propName} | {propType} | {constraints} | {notesStr} |");
            }

            lines.Add("");
            idx++;
        }

        return lines;
    }

    private static List<string> EmitAuthorizationMatrix(SchemaModel ir, Dictionary<string, object>? manifest, Coverage coverage)
    {
        var lines = new List<string>();
        lines.Add("## 4. Authorization Matrix");
        lines.Add("");

        var entities = ir.Entities ?? new();
        var manifestEndpoints = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);

        if (manifest != null && manifest.TryGetValue("Endpoints", out var epObj) && epObj is List<object> epList)
        {
            foreach (var ep in epList)
            {
                if (ep is Dictionary<string, object> epDict &&
                    epDict.TryGetValue("Entity", out var entVal) && entVal is string entName)
                {
                    manifestEndpoints[entName] = epDict;
                }
            }
        }

        lines.Add("### CRUD Authorization");
        lines.Add("");
        if (manifest != null)
            lines.Add("> Roles shown are those **enforced** by the runtime, read from api-manifest.json.");
        else
            lines.Add("> Roles shown are those **declared** in the IR schema. No manifest was supplied, so enforcement is unverified.");
        lines.Add("");

        lines.Add("| Entity | GET | GET_BY_ID | POST | PUT | DELETE |");
        lines.Add("| --- | --- | --- | --- | --- | --- |");

        bool hasRoleData = false;
        foreach (var entity in entities)
        {
            var row = new List<string> { EscapePipe(entity.Name) };
            var allMethods = new[] { "GET", "GET_BY_ID", "POST", "PUT", "DELETE" };

            List<string> enabledMethods;
            Dictionary<string, List<string>> rolesDict;

            if (manifest != null && manifestEndpoints.TryGetValue(entity.Name, out var mep))
            {
                enabledMethods = GetMethods(mep);
                rolesDict = GetRolesDict(mep);
            }
            else
            {
                enabledMethods = entity.ApiEnabledMethods ?? new();
                rolesDict = entity.ApiRoles ?? new();
            }

            foreach (var method in allMethods)
            {
                if (enabledMethods.Contains(method))
                {
                    if (rolesDict.TryGetValue(method, out var roles) && roles.Count > 0)
                    {
                        row.Add(string.Join(", ", roles));
                        hasRoleData = true;
                    }
                    else
                        row.Add("—");
                }
                else
                    row.Add("—");
            }

            lines.Add("| " + string.Join(" | ", row) + " |");
        }

        lines.Add("");
        lines.Add("### Role-Based Access");
        lines.Add("");
        if (manifest != null)
            lines.Add("> Roles shown are those **enforced** by the runtime, read from api-manifest.json.");
        else
            lines.Add("> Roles shown are those **declared** in the IR schema. No manifest was supplied, so enforcement is unverified.");
        lines.Add("");

        var allRoles = new HashSet<string>();
        foreach (var entity in entities)
        {
            var rolesDict = manifest != null && manifestEndpoints.TryGetValue(entity.Name, out var mep)
                ? GetRolesDict(mep)
                : (entity.ApiRoles ?? new());
            foreach (var roleList in rolesDict.Values)
                allRoles.UnionWith(roleList);
        }

        lines.Add("| Role | Readable Entities | Writable Entities |");
        lines.Add("| --- | --- | --- |");

        foreach (var role in allRoles.OrderBy(r => r))
        {
            var readable = new List<string>();
            var writable = new List<string>();

            foreach (var entity in entities)
            {
                var rolesDict = manifest != null && manifestEndpoints.TryGetValue(entity.Name, out var mep)
                    ? GetRolesDict(mep)
                    : (entity.ApiRoles ?? new());

                if (rolesDict.TryGetValue("GET", out var getRoles) && getRoles.Contains(role))
                    readable.Add(entity.Name);
                if ((rolesDict.TryGetValue("POST", out var postRoles) && postRoles.Contains(role)) ||
                    (rolesDict.TryGetValue("PUT", out var putRoles) && putRoles.Contains(role)) ||
                    (rolesDict.TryGetValue("DELETE", out var delRoles) && delRoles.Contains(role)))
                    writable.Add(entity.Name);
            }

            string readableStr = readable.Count > 0 ? string.Join(", ", readable) : "—";
            string writableStr = writable.Count > 0 ? string.Join(", ", writable) : "—";
            lines.Add($"| {EscapePipe(role)} | {readableStr} | {writableStr} |");
        }

        lines.Add("");

        var ownerScoped = entities.Where(e => e.OwnerScoped).ToList();
        if (ownerScoped.Count > 0)
        {
            lines.Add("### Owner-Scoped Access Control");
            lines.Add("");
            foreach (var entity in ownerScoped)
            {
                lines.Add($"**{entity.Name}**:");
                var exempt = entity.OwnerExemptRoles ?? new();
                var readExempt = entity.OwnerReadExemptRoles ?? new();
                if (exempt.Count > 0)
                    lines.Add($"  - Write exempt roles: {string.Join(", ", exempt)}");
                if (readExempt.Count > 0)
                    lines.Add($"  - Read exempt roles: {string.Join(", ", readExempt)}");
            }
            lines.Add("");
        }

        if (hasRoleData || allRoles.Count > 0)
            coverage.Claim("rbac", "Role-based access control (CRUD and resource-level)");

        return lines;
    }

    private static Dictionary<string, List<string>> GetRolesDict(Dictionary<string, object> mep)
    {
        var result = new Dictionary<string, List<string>>();
        if (mep.TryGetValue("Roles", out var rolesObj) && rolesObj is Dictionary<string, object> rolesDict)
        {
            foreach (var kvp in rolesDict)
            {
                if (kvp.Key.ToString()?.ToUpper() is string method && kvp.Value is List<object> roles)
                {
                    result[method] = roles.Select(r => r?.ToString() ?? "").Where(s => !string.IsNullOrEmpty(s)).ToList();
                }
            }
        }
        return result;
    }

    private static List<string> GetMethods(Dictionary<string, object> mep)
    {
        if (mep.TryGetValue("Methods", out var methods) && methods is List<object> methodsList)
            return methodsList.Select(m => m?.ToString() ?? "").Where(s => !string.IsNullOrEmpty(s)).ToList();
        return new();
    }

    private static List<string> EmitDataProtection(SchemaModel ir, Coverage coverage)
    {
        var lines = new List<string>();
        lines.Add("## 5. Data Protection Register");
        lines.Add("");

        var entities = ir.Entities ?? new();
        var protectedProps = new List<(string entity, string property, string category, string protection, string effect)>();

        foreach (var entity in entities)
        {
            foreach (var prop in entity.Properties ?? new())
            {
                var attrs = prop.Attributes ?? new();
                var sensitive = prop.SensitiveCategory;

                bool hasProtection = attrs.Any(a => a == "Encrypt" || a == "Mask" || a == "PiiEmail" || a == "Phone") || !string.IsNullOrEmpty(sensitive);

                if (hasProtection)
                {
                    var protection = new List<string>();
                    var effect = new List<string>();

                    if (attrs.Contains("Encrypt"))
                    {
                        protection.Add("Encrypt");
                        effect.Add("stored as ciphertext; unreadable if the key is rotated");
                    }
                    if (attrs.Contains("Mask"))
                    {
                        protection.Add("Mask");
                        effect.Add($"redacted in responses unless the caller holds view:{sensitive ?? "pii"} or an entitled role");
                    }
                    if (attrs.Contains("PiiEmail")) protection.Add("PiiEmail");
                    if (attrs.Contains("Phone")) protection.Add("Phone");
                    if (!string.IsNullOrEmpty(sensitive) && !attrs.Contains("Encrypt") && !attrs.Contains("Mask")
                        && !attrs.Contains("PiiEmail") && !attrs.Contains("Phone"))
                        protection.Add(sensitive);

                    protectedProps.Add((
                        entity.Name,
                        prop.Name,
                        sensitive ?? "—",
                        string.Join(", ", protection),
                        string.Join("; ", effect)
                    ));
                }
            }
        }

        if (protectedProps.Count > 0)
        {
            lines.Add("| Entity | Property | Category | Protection | Effect |");
            lines.Add("| --- | --- | --- | --- | --- |");
            foreach (var (ent, prop, cat, prot, eff) in protectedProps)
            {
                lines.Add($"| {EscapePipe(ent)} | {EscapePipe(prop)} | {EscapePipe(cat)} | {EscapePipe(prot)} | {EscapePipe(eff)} |");
            }
            lines.Add("");
        }
        else
        {
            lines.Add("None declared.");
            lines.Add("");
        }

        var multiTenant = entities.Where(e => e.MultiTenant).ToList();
        if (multiTenant.Count > 0)
        {
            lines.Add("### Multi-Tenancy & Isolation");
            lines.Add("");
            lines.Add($"{multiTenant.Count} entities are multi-tenant.");
            var tenantProps = new HashSet<string>(multiTenant.Select(e => e.TenantProperty).Where(t => !string.IsNullOrEmpty(t)));
            if (tenantProps.Count > 0)
                lines.Add($"Tenant property names used: {string.Join(", ", tenantProps.OrderBy(t => t))}");
            lines.Add("");
        }

        if (protectedProps.Count > 0)
            coverage.Claim("properties", "Property types, constraints, and protection levels");

        return lines;
    }

    private static List<string> EmitWorkflows(SchemaModel ir, Coverage coverage)
    {
        var lines = new List<string>();
        lines.Add("## 6. Workflow Specifications");
        lines.Add("");

        var workflows = ir.Workflows ?? new();
        if (workflows.Count == 0)
        {
            lines.Add("None declared.");
            lines.Add("");
            return lines;
        }

        coverage.Claim("workflows", "Workflow state machines and transitions");

        int wfIdx = 1;
        foreach (var workflow in workflows)
        {
            lines.Add($"### 6.{wfIdx} {workflow.Id}");
            lines.Add("");
            string activeStr = workflow.IsActive ? "Yes" : "No";
            lines.Add($"Entity: {EscapePipe(workflow.Entity ?? "—")} | Version: {EscapePipe(workflow.Version ?? "—")} | Active: {activeStr}");
            lines.Add("");

            lines.Add("```mermaid");
            lines.Add("stateDiagram-v2");

            var states = workflow.States ?? new();
            var transitions = workflow.Transitions ?? new();
            var choiceNodes = workflow.ChoiceNodes ?? new();

            var initialState = states.FirstOrDefault(s => s.IsInitial)?.Name;
            if (!string.IsNullOrEmpty(initialState))
                lines.Add($"    [*] --> {initialState}");

            foreach (var choice in choiceNodes)
                lines.Add($"    state {choice.Id} <<choice>>");

            foreach (var trans in transitions)
            {
                string from = trans.FromState ?? "?";
                string to = trans.ToState ?? "?";
                string trigger = trans.Trigger ?? "N/A";
                lines.Add($"    {from} --> {to} : {trigger}");
            }

            foreach (var choice in choiceNodes)
            {
                var branches = choice.Branches ?? new();
                foreach (var branch in branches)
                {
                    string target = branch.TargetState ?? "?";
                    string condStr = "condition";
                    if (branch.Condition != null)
                    {
                        var prop = branch.Condition.Property ?? "?";
                        var op = branch.Condition.Operator ?? "?";
                        var val = string.IsNullOrEmpty(branch.Condition.Value) ? "(empty string)" : branch.Condition.Value;
                        condStr = $"{prop} {op} {val} ({branch.Condition.Source ?? "entity"})";
                    }
                    lines.Add($"    {choice.Id} --> {target} : {condStr}");
                }
                string defaultState = choice.DefaultState ?? "?";
                lines.Add($"    {choice.Id} --> {defaultState} : default");
            }

            lines.Add("```");
            lines.Add("");

            if (transitions.Count > 0)
            {
                lines.Add("#### Transitions");
                lines.Add("");
                lines.Add("| Trigger | From | To | Required Roles | Guards |");
                lines.Add("| --- | --- | --- | --- | --- |");

                foreach (var trans in transitions)
                {
                    string trigger = EscapePipe(trans.Trigger ?? "—");
                    string from = EscapePipe(trans.FromState ?? "—");
                    string to = EscapePipe(trans.ToState ?? "—");
                    string roles = (trans.RequiredRoles?.Count ?? 0) > 0 ? string.Join(", ", trans.RequiredRoles) : "—";

                    string guards = "—";
                    if ((trans.Conditions?.Count ?? 0) > 0)
                    {
                        var guardStrs = trans.Conditions.Select(c =>
                        {
                            var prop = c.Property ?? "?";
                            var op = c.Operator ?? "?";
                            var val = string.IsNullOrEmpty(c.Value) ? "(empty string)" : c.Value;
                            var src = c.Source ?? "entity";
                            return $"{prop} {op} {val} ({src})";
                        });
                        guards = string.Join("; ", guardStrs);
                    }

                    lines.Add($"| {trigger} | {from} | {to} | {roles} | {guards} |");
                }

                lines.Add("");
            }

            if (choiceNodes.Count > 0)
            {
                lines.Add("#### Choice Nodes");
                lines.Add("");
                lines.Add("| Node ID | Branches | Default |");
                lines.Add("| --- | --- | --- |");

                foreach (var choice in choiceNodes)
                {
                    var branches = choice.Branches ?? new();
                    var branchStrs = new List<string>();
                    foreach (var b in branches)
                    {
                        string target = b.TargetState ?? "?";
                        string condStr = "condition";
                        if (b.Condition != null)
                        {
                            var prop = b.Condition.Property ?? "?";
                            var op = b.Condition.Operator ?? "?";
                            var val = string.IsNullOrEmpty(b.Condition.Value) ? "(empty string)" : b.Condition.Value;
                            condStr = $"{prop} {op} {val} ({b.Condition.Source ?? "entity"})";
                        }
                        branchStrs.Add($"{condStr} → {target}");
                    }
                    string branchStr = branchStrs.Count > 0 ? string.Join("; ", branchStrs) : "—";
                    string defaultState = EscapePipe(choice.DefaultState ?? "—");
                    lines.Add($"| {EscapePipe(choice.Id)} | {EscapePipe(branchStr)} | {defaultState} |");
                }

                lines.Add("");
            }

            wfIdx++;
        }

        return lines;
    }

    private static List<string> EmitApiSurface(SchemaModel ir, Dictionary<string, object>? manifest, Coverage coverage)
    {
        var lines = new List<string>();
        lines.Add("## 7. API Surface");
        lines.Add("");

        var entities = ir.Entities ?? Enumerable.Empty<Entity>();

        // Claim custom endpoints coverage if any are present
        if (ir.CustomEndpoints?.Count > 0)
        {
            coverage.Claim("custom_endpoints", "Custom endpoint routes, methods, roles, and filters");
        }

        // CRUD routes
        if (manifest != null)
        {
            var manifestEndpoints = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);

            if (manifest.TryGetValue("Endpoints", out var endPointsObj) && endPointsObj is List<object> endPointsList)
            {
                foreach (var ep in endPointsList)
                {
                    if (ep is Dictionary<string, object> epDict &&
                        epDict.TryGetValue("Entity", out var entityValue) &&
                        entityValue is string entityName)
                    {
                        manifestEndpoints[entityName] = epDict;
                    }
                }
            }

            lines.Add("### Generated CRUD Routes");
            lines.Add("");
            lines.Add("| Entity | Route | Methods |");
            lines.Add("| --- | --- | --- |");

            foreach (var entity in entities)
            {
                if (manifestEndpoints.TryGetValue(entity.Name, out var me))
                {
                    string route = EscapePipe(me.GetValueOrDefault("Route")?.ToString() ?? "—");
                    string methods = me.GetValueOrDefault("Methods") switch
                    {
                        List<object> m => string.Join(", ", m.Select(o => o?.ToString() ?? "")),
                        _ => "—"
                    };

                    lines.Add($"| {EscapePipe(entity.Name)} | {route} | {methods} |");
                }
            }

            lines.Add("");
            lines.Add("_Note: append `/{id}` to route for GET_BY_ID, PUT, DELETE methods._");
            lines.Add("");
        }
        else
        {
            lines.Add("### Generated CRUD Routes");
            lines.Add("");
            lines.Add("> **Not derivable.** Route is assigned by the compiler; provide api-manifest.json to see literal routes.");
            lines.Add("");
            lines.Add("| Entity | apiEnabledMethods | apiRoles |");
            lines.Add("| --- | --- | --- |");

            foreach (var entity in entities)
            {
                var methods = string.Join(", ", entity.ApiEnabledMethods ?? Enumerable.Empty<string>());

                var rolesAll = new HashSet<string>();
                if (entity.ApiRoles != null)
                {
                    foreach (var rList in entity.ApiRoles.Values)
                    {
                        rolesAll.UnionWith(rList);
                    }
                }

                var roles = string.Join(", ", rolesAll.OrderBy(r => r));

                lines.Add($"| {EscapePipe(entity.Name)} | {methods} | {roles} |");
            }

            lines.Add("");
        }

        // Custom endpoints
        if (ir.CustomEndpoints?.Count > 0)
        {
            lines.Add("### Custom Endpoints");
            lines.Add("");

            if (manifest != null)
            {
                lines.Add("> Roles shown are those **enforced** by the runtime, read from api-manifest.json.");
            }
            else
            {
                lines.Add("> Roles shown are those **declared** in the IR schema. No manifest was supplied, so enforcement is unverified.");
            }

            lines.Add("");
            lines.Add("| Route | Method | Type | Entity | Request | Roles | Filter | Assignments | Rules |");
            lines.Add("| --- | --- | --- | --- | --- | --- | --- | --- | --- |");

            foreach (var ep in ir.CustomEndpoints)
            {
                string route = EscapePipe(ep.Route ?? "—");
                string method = EscapePipe(ep.Method ?? "—");
                string opType = EscapePipe(ep.OperationType ?? "—");
                string entity = EscapePipe(ep.TargetEntity ?? "—");
                string reqType = EscapePipe(ep.RequestType ?? "—");

                var rolesList = new List<string>(ep.Roles ?? Enumerable.Empty<string>());
                string roles = string.Join(", ", rolesList);

                // Filter
                string filterStr;
                if (!string.IsNullOrEmpty(ep.FilterField))
                {
                    filterStr = $"{ep.FilterField} {EscapePipe(ep.FilterOperator ?? "?")} {EscapePipe(ep.FilterSourceValue ?? "?")}";
                }
                else
                {
                    filterStr = "—";
                }

                // Assignments
                string assignStr;
                if (ep.Assignments != null && ep.Assignments.Count > 0)
                {
                    var assignmentList = ep.Assignments.Select(a => $"{EscapePipe(a.EntityProperty ?? "?")} <- {EscapePipe(a.SourceValue ?? "?")}");
                    assignStr = string.Join("; ", assignmentList);
                }
                else
                {
                    assignStr = "—";
                }

                // Rules
                string rulesStr;
                if (ep.BusinessRules != null && ep.BusinessRules.Count > 0)
                {
                    rulesStr = string.Join(", ", ep.BusinessRules);
                }
                else
                {
                    rulesStr = "—";
                }

                lines.Add($"| {route} | {method} | {opType} | {entity} | {reqType} | {roles} | {filterStr} | {assignStr} | {rulesStr} |");
            }

            lines.Add("");
        }

        // Schema/Manifest Divergence section
        lines.Add("### Schema/Manifest Divergence & Enforcement Verification");
        lines.Add("");

        if (manifest != null)
        {
            // Run divergence check
            var check = new DivergenceCheck();
            check.Run(ir, manifest);

            if (!check.Divergences.Any())
            {
                lines.Add("**No divergences detected.** Comparison verified:");
                lines.Add($"- {check.EntitiesChecked} CRUD endpoint(s) compared");
                lines.Add($"- {check.CustomEndpointsChecked} custom endpoint(s) compared");
                lines.Add($"- {check.TransitionsDerived} workflow transition endpoint(s) excluded (compiler-derived)");
                lines.Add("");
            }
            else
            {
                lines.Add("**Divergences detected.** Review and reconcile:");
                lines.Add("");

                // Separate by kind
                var enforcementGaps = check.Divergences.Where(d => d.Kind == "enforcement_gap").ToList();
                var docInconsistencies = check.Divergences.Where(d => d.Kind == "documentation_inconsistency").ToList();

                if (enforcementGaps.Any())
                {
                    lines.Add("#### Enforcement Gaps");
                    lines.Add("");
                    lines.Add("| Element | IR Declares | Manifest Enforces | Consequence |");
                    lines.Add("| --- | --- | --- | --- |");

                    foreach (var div in enforcementGaps)
                    {
                        lines.Add($"| {EscapePipe(div.Element)} | {EscapePipe(div.IrValue)} | {EscapePipe(div.ManifestValue)} | {EscapePipe(div.Consequence)} |");
                    }

                    lines.Add("");
                }

                if (docInconsistencies.Any())
                {
                    lines.Add("#### Documentation Inconsistencies (Business Rules)");
                    lines.Add("");
                    lines.Add("| Element | IR Declares | Manifest Records | Note |");
                    lines.Add("| --- | --- | --- | --- |");

                    foreach (var div in docInconsistencies)
                    {
                        string note = DivergenceCheck.DocumentationInconsistencyNote;
                        lines.Add($"| {EscapePipe(div.Element)} | {EscapePipe(div.IrValue)} | {EscapePipe(div.ManifestValue)} | {note} |");
                    }

                    lines.Add("");
                }

                lines.Add($"Comparison included {check.EntitiesChecked} CRUD endpoint(s), {check.CustomEndpointsChecked} custom endpoint(s), and excluded {check.TransitionsDerived} compiler-derived workflow transition endpoints.");
                lines.Add("");
            }
            // Only claim divergence_check if divergences were actually found
            if (check.Divergences.Any())
            {
                coverage.Claim("divergence_check", "Schema/Manifest divergences detected");
            }
        }
        else
        {
            lines.Add("> **Enforcement could not be verified.** No manifest was provided. A manifest is required to compare declared vs. enforced roles, methods, and business rules. Absence of a manifest is not evidence of correctness; it means verification was not performed.");
            lines.Add("");
        }

        return lines;
    }

    private static List<string> EmitEventsRealtime(SchemaModel ir, Coverage coverage)
    {
        var lines = new List<string>();
        lines.Add("## 8. Event & Real-Time Catalog");
        lines.Add("");

        var entities = ir.Entities ?? new();
        var dtos = ir.Dtos ?? new();

        var kafkaEntities = entities.Where(e => !string.IsNullOrEmpty(e.KafkaTopic)).ToList();
        var kafkaDtos = dtos.Where(d => !string.IsNullOrEmpty(d.KafkaTopic)).ToList();

        if (kafkaEntities.Count > 0 || kafkaDtos.Count > 0)
        {
            coverage.Claim("events", "Real-time and event-driven capabilities");
            lines.Add("### Kafka Outbox");
            lines.Add("");
            lines.Add("| Source | Topic |");
            lines.Add("| --- | --- |");

            foreach (var entity in kafkaEntities)
                lines.Add($"| {EscapePipe(entity.Name)} (entity) | {EscapePipe(entity.KafkaTopic)} |");

            foreach (var dto in kafkaDtos)
                lines.Add($"| {EscapePipe(dto.Name)} (DTO) | {EscapePipe(dto.KafkaTopic)} |");

            lines.Add("");
            lines.Add("> **Not derivable from the schema.** Document the event contract (field names, types, transformations) and consumer implementation details.");
            lines.Add("");
        }

        var realtimeEntities = entities.Where(e => e.RealTime).ToList();
        if (realtimeEntities.Count > 0)
        {
            coverage.Claim("events", "Real-time and event-driven capabilities");
            lines.Add("### Real-Time Subscriptions");
            lines.Add("");
            lines.Add("| Entity | Roles |");
            lines.Add("| --- | --- |");

            foreach (var entity in realtimeEntities)
            {
                string roles = (entity.RealTimeRoles?.Count ?? 0) > 0 ? string.Join(", ", entity.RealTimeRoles) : "—";
                lines.Add($"| {EscapePipe(entity.Name)} | {roles} |");
            }

            lines.Add("");
        }

        if (kafkaEntities.Count == 0 && kafkaDtos.Count == 0 && realtimeEntities.Count == 0)
        {
            lines.Add("None declared.");
            lines.Add("");
        }

        return lines;
    }

    private static List<string> EmitPersistence(SchemaModel ir, Coverage coverage)
    {
        var lines = new List<string>();
        lines.Add("## 9. Persistence & Performance");
        lines.Add("");

        var entities = ir.Entities ?? new();

        bool indexesPresent = entities.Any(e => (e.Indexes?.Count ?? 0) > 0);
        bool cachingPresent = entities.Any(e => (e.ApiCaching?.Count ?? 0) > 0);
        bool archivalPresent = entities.Any(e => e.ArchiveThresholdYears > 0 || e.Partitioned);

        if (indexesPresent)
        {
            lines.Add("### Indexes");
            lines.Add("");
            lines.Add("| Entity | Index Name | Fields | Unique |");
            lines.Add("| --- | --- | --- | --- |");

            foreach (var entity in entities)
            {
                foreach (var idx in entity.Indexes ?? new())
                {
                    string name = string.IsNullOrEmpty(idx.Name) ? "—" : EscapePipe(idx.Name);
                    string fields = (idx.Fields?.Count ?? 0) > 0 ? string.Join(", ", idx.Fields) : "—";
                    string unique = idx.Unique ? "Yes" : "No";
                    lines.Add($"| {EscapePipe(entity.Name)} | {name} | {EscapePipe(fields)} | {unique} |");
                }
            }

            lines.Add("");
        }

        if (cachingPresent)
        {
            lines.Add("### Caching");
            lines.Add("");
            lines.Add("| Entity | Method | TTL (seconds) |");
            lines.Add("| --- | --- | --- |");

            foreach (var entity in entities)
            {
                foreach (var kvp in entity.ApiCaching ?? new())
                {
                    string ttl = kvp.Value?.Enabled == true ? kvp.Value.TtlSeconds.ToString() : "—";
                    lines.Add($"| {EscapePipe(entity.Name)} | {EscapePipe(kvp.Key)} | {ttl} |");
                }
            }

            lines.Add("");
        }

        if (archivalPresent)
        {
            lines.Add("### Archival & Partitioning");
            lines.Add("");
            lines.Add("| Entity | Archived After | Partitioned |");
            lines.Add("| --- | --- | --- |");

            foreach (var entity in entities)
            {
                // Only show archival rows for partitioned entities.
                // Archive threshold has no effect if the entity is not partitioned — PocoGenerator
                // emits [Partitioned(years)] only for partitioned entities, leaving the value inert otherwise.
                if (entity.Partitioned)
                {
                    string years = entity.ArchiveThresholdYears > 0 ? $"{entity.ArchiveThresholdYears} years" : "—";
                    string partitioned = "Yes";
                    lines.Add($"| {EscapePipe(entity.Name)} | {years} | {partitioned} |");
                }
            }

            lines.Add("");
        }

        if (!indexesPresent && !cachingPresent && !archivalPresent)
        {
            lines.Add("None declared.");
            lines.Add("");
        }

        if (indexesPresent || cachingPresent || archivalPresent)
            coverage.Claim("persistence", "Persistence (indexes, caching, archival policies)");

        return lines;
    }

    private static List<string> EmitExternalDependencies(SchemaModel ir, Coverage coverage)
    {
        var lines = new List<string>();
        lines.Add("## 10. External Dependencies");
        lines.Add("");

        var connectors = ir.Connectors ?? new();
        if (connectors.Count == 0)
        {
            lines.Add("None declared.");
            lines.Add("");
            return lines;
        }

        coverage.Claim("integrations", "External system integrations");

        lines.Add("| Name | Type | Base URL | Auth | Timeout | Retries | Credential Source | Literals Present |");
        lines.Add("| --- | --- | --- | --- | --- | --- | --- | --- |");

        bool hasLiterals = false;

        foreach (var conn in connectors)
        {
            string name = EscapePipe(conn.Name);
            string connType = EscapePipe(conn.Type ?? "—");
            string baseUrl = EscapePipe(conn.BaseUrl ?? "—");
            string auth = EscapePipe(conn.AuthType ?? "—");
            string timeout = EscapePipe(conn.TimeoutSeconds.ToString());
            string retries = EscapePipe(conn.MaxRetries.ToString());

            var placeholders = new List<string>();
            var literals = new List<string>();

            // Check credential fields - NEVER print values, only field names for literals
            if (!string.IsNullOrEmpty(conn.Token))
            {
                if (SchemaValidator.IsSecretReference(conn.Token))
                    placeholders.Add(conn.Token);
                else
                {
                    literals.Add("token");
                    hasLiterals = true;
                }
            }

            if (!string.IsNullOrEmpty(conn.ApiKey))
            {
                if (SchemaValidator.IsSecretReference(conn.ApiKey))
                    placeholders.Add(conn.ApiKey);
                else
                {
                    literals.Add("apiKey");
                    hasLiterals = true;
                }
            }

            if (!string.IsNullOrEmpty(conn.Username))
            {
                if (SchemaValidator.IsSecretReference(conn.Username))
                    placeholders.Add(conn.Username);
                else
                {
                    literals.Add("username");
                    hasLiterals = true;
                }
            }

            if (!string.IsNullOrEmpty(conn.Password))
            {
                if (SchemaValidator.IsSecretReference(conn.Password))
                    placeholders.Add(conn.Password);
                else
                {
                    literals.Add("password");
                    hasLiterals = true;
                }
            }

            string credSource = placeholders.Count > 0 ? string.Join(", ", placeholders) : "None declared.";
            string literalsStr = literals.Count > 0 ? string.Join(", ", literals) : "—";

            lines.Add($"| {name} | {connType} | {baseUrl} | {auth} | {timeout} | {retries} | {EscapePipe(credSource)} | {literalsStr} |");
        }

        lines.Add("");

        if (hasLiterals)
        {
            lines.Add("> **Note**: Some credential fields contain values committed into the schema rather than resolved from environment variables. A security reviewer should confirm each field is intended to be non-secret.");
            lines.Add("");
        }

        // Only emit API Key Header when AuthType is ApiKey. A header name is meaningless
        // under Bearer or Basic auth. Check for connectors that have meaningful details.
        bool hasExtraDetails = connectors.Any(c =>
            (c.AuthType == "ApiKey" && !string.IsNullOrEmpty(c.ApiKeyHeaderName)) ||
            !string.IsNullOrEmpty(c.SoapAction));

        if (hasExtraDetails)
        {
            lines.Add("### Connector Details");
            lines.Add("");
            foreach (var conn in connectors)
            {
                bool hasDetails = (conn.AuthType == "ApiKey" && !string.IsNullOrEmpty(conn.ApiKeyHeaderName)) ||
                                  !string.IsNullOrEmpty(conn.SoapAction);
                if (hasDetails)
                {
                    lines.Add($"**{conn.Name}**:");
                    if (conn.AuthType == "ApiKey" && !string.IsNullOrEmpty(conn.ApiKeyHeaderName))
                        lines.Add($"- API Key Header: `{EscapePipe(conn.ApiKeyHeaderName)}`");
                    if (!string.IsNullOrEmpty(conn.SoapAction))
                        lines.Add($"- SOAP Action: `{EscapePipe(conn.SoapAction)}`");
                    lines.Add("");
                }
            }
        }

        return lines;
    }

    private static List<string> EmitExtensionPoints(SchemaModel ir, Coverage coverage)
    {
        var lines = new List<string>();
        lines.Add("## 11. Extension Points");
        lines.Add("");

        var endpoints = ir.CustomEndpoints ?? new();
        var entities = ir.Entities ?? new();

        var requestTypesSet = new HashSet<string>();
        var requestTypesInfo = new Dictionary<string, (string operationType, string route)>();
        foreach (var ep in endpoints)
        {
            if (!string.IsNullOrEmpty(ep.RequestType))
            {
                requestTypesSet.Add(ep.RequestType);
                requestTypesInfo[ep.RequestType] = (ep.OperationType ?? "?", ep.Route ?? "—");
            }
        }

        var rulesBinding = new Dictionary<string, List<string>>();
        foreach (var entity in entities)
        {
            foreach (var kvp in entity.ApiBusinessRules ?? new())
            {
                foreach (var rule in kvp.Value ?? new())
                {
                    if (!rulesBinding.ContainsKey(rule))
                        rulesBinding[rule] = new();
                    rulesBinding[rule].Add($"entity {entity.Name} {kvp.Key}");
                }
            }
        }

        foreach (var ep in endpoints)
        {
            foreach (var rule in ep.BusinessRules ?? new())
            {
                if (!rulesBinding.ContainsKey(rule))
                    rulesBinding[rule] = new();
                rulesBinding[rule].Add($"endpoint {ep.Route ?? "—"}");
            }
        }

        if (requestTypesSet.Count > 0)
        {
            lines.Add("### Custom Endpoint Request Types");
            lines.Add("");
            lines.Add("| Request Type | Kind | Endpoint | Implementation | Authored |");
            lines.Add("| --- | --- | --- | --- | --- |");

            foreach (var reqType in requestTypesSet.OrderBy(r => r))
            {
                if (requestTypesInfo.TryGetValue(reqType, out var info))
                {
                    string opType = EscapePipe(info.operationType);
                    string route = EscapePipe(info.route);
                    string impl = EscapePipe($"Generated/Commands/{reqType}.cs");
                    lines.Add($"| {EscapePipe(reqType)} | {opType} | {route} | {impl} | Auto-generated |");
                }
            }

            lines.Add("");
        }

        if (rulesBinding.Count > 0)
        {
            lines.Add("### Business Rules");
            lines.Add("");
            lines.Add("| Rule Name | Kind | Bound via | Implementation | Authored |");
            lines.Add("| --- | --- | --- | --- | --- |");

            foreach (var rule in rulesBinding.Keys.OrderBy(r => r))
            {
                var bindings = rulesBinding[rule];
                string boundVia = EscapePipe(string.Join("; ", bindings));
                string impl = EscapePipe($"Generated/Rules/{rule}.cs");
                lines.Add($"| {EscapePipe(rule)} | Validation | {boundVia} | {impl} | Scaffold (hand-written) |");
            }

            lines.Add("");
            lines.Add("> **Not derivable from the schema.** Business rule bodies are hand-written and their validation logic cannot be inferred from the schema.");
            lines.Add("");
        }

        if (requestTypesSet.Count == 0 && rulesBinding.Count == 0)
        {
            lines.Add("None declared.");
            lines.Add("");
        }

        return lines;
    }

    private static List<string> EmitGaps(List<Gap> activeGaps, Coverage coverage)
    {
        var lines = new List<string>();
        lines.Add("## 12. Gaps for the Author");
        lines.Add("");

        if (activeGaps.Any())
        {
            lines.Add("The following topics cannot be derived from the schema and require hand-authored documentation:");
            lines.Add("");

            // Sort by (section is null, section, topic)
            var sorted = activeGaps.OrderBy(g => g.Section is null).ThenBy(g => g.Section).ThenBy(g => g.Topic);

            foreach (var gap in sorted)
            {
                if (gap.Section is null)
                {
                    lines.Add($"- **Document-level — {gap.Topic}**: {gap.Detail}");
                }
                else
                {
                    lines.Add($"- **Section {gap.Section}. {gap.Topic}**: {gap.Detail}");
                }
            }
            lines.Add("");
        }
        else
        {
            lines.Add("All topics are derivable from the schema.");
            lines.Add("");
        }

        // Add pointer to section 7 if divergences were found
        if (coverage.Claims.Any(c => c.Key == "divergence_check"))
        {
            lines.Add("### Schema/Manifest Divergence");
            lines.Add("");
            lines.Add("Divergences were detected between the IR schema and the manifest. See **Section 7 — Schema/Manifest Divergence & Enforcement Verification** for details and recommended actions.");
            lines.Add("");
        }

        return lines;
    }

    /// <summary>
    /// Generates a technical reference markdown document from a Foundry IR schema and optional manifest.
    /// </summary>
    public static string ExportMarkdown(SchemaModel schema, JsonNode? manifest, ReferenceSource source)
    {
        // Parse manifest - deserialize to JsonElement first to ensure proper type conversions
        Dictionary<string, object>? parsedManifest = null;
        if (manifest != null)
        {
            try
            {
                var json = manifest.ToJsonString();
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                parsedManifest = ConvertJsonElementToDictionary(doc.RootElement);
            }
            catch { /* Ignore parse errors */ }
        }

        // Create coverage tracker
        var coverage = new Coverage();

        // Compute active_gaps: all GAPS except api_routes if manifest provided
        var activeGaps = parsedManifest != null
            ? GapsRegistry.Gaps.Where(g => g.Key != "api_routes").ToList()
            : GapsRegistry.Gaps.ToList();

        // Build sections 2-11
        var sections2To11 = new List<string>();
        sections2To11.AddRange(EmitOverview(schema, coverage));
        sections2To11.AddRange(EmitDomainModel(schema, coverage));
        sections2To11.AddRange(EmitAuthorizationMatrix(schema, parsedManifest, coverage));
        sections2To11.AddRange(EmitDataProtection(schema, coverage));
        sections2To11.AddRange(EmitWorkflows(schema, coverage));
        sections2To11.AddRange(EmitApiSurface(schema, parsedManifest, coverage));
        sections2To11.AddRange(EmitEventsRealtime(schema, coverage));
        sections2To11.AddRange(EmitPersistence(schema, coverage));
        sections2To11.AddRange(EmitExternalDependencies(schema, coverage));
        sections2To11.AddRange(EmitExtensionPoints(schema, coverage));

        // Contradiction guard: no coverage key should overlap with any gap key
        var coverageKeys = new HashSet<string>(coverage.Claims.Select(c => c.Key));
        var gapKeys = new HashSet<string>(activeGaps.Select(g => g.Key));
        var overlap = coverageKeys.Intersect(gapKeys).ToList();

        if (overlap.Any())
        {
            var overlapList = string.Join(", ", overlap.OrderBy(x => x));
            throw new ReferenceExportException(
                $"Coverage and gap overlap on keys: {overlapList}. A previous version shipped a document that claimed and disclaimed the same topics. Check which topics are genuinely covered.");
        }

        // Build output
        var output = new List<string>();
        output.AddRange(EmitTitle(schema, source.IrFileName, source.IrSha256, source.ManifestFileName, source.ManifestSha256));
        output.AddRange(EmitScope(activeGaps, coverage));
        output.AddRange(sections2To11);
        output.AddRange(EmitGaps(activeGaps, coverage));

        return string.Join("\n", output);
    }
}

public sealed record ReferenceSource(
    string IrFileName,
    string IrSha256,
    string? ManifestFileName,
    string? ManifestSha256);
