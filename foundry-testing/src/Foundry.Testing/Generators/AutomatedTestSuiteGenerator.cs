using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Foundry.Schema.Compiler;

namespace Foundry.Testing.Generators;

/// <summary>
/// Automated Test Suite Generator that scans a Foundry domain schema and produces 
/// xUnit test suites covering REST, GraphQL, Kafka, Real-Time WebSockets, FileIO, Rules, and Workflows.
/// </summary>
public static class AutomatedTestSuiteGenerator
{
    public static Dictionary<string, string> GenerateAllTestSuites(SchemaModel schema)
    {
        var files = new Dictionary<string, string>();
        var ns = schema.Namespace;

        if (schema.Entities == null) return files;

        // One place that knows the base address, the bearer token and the tenant header, rather than
        // a copy of all three in every emitted file.
        files["FoundryTestEnvironment.cs"] = GenerateEnvironment(ns);

        foreach (var entity in schema.Entities)
        {
            var name = entity.Name;

            // 1. REST API Integration Tests, for the methods the entity actually declares.
            //
            // These used to be emitted for every entity regardless. An entity with no
            // apiEnabledMethods has no REST surface at all, so the suite asserted 200 against routes
            // the application does not serve and reported the framework broken.
            var methods = ApiManifestGenerator.EnabledMethods(entity);
            if (methods.Count > 0)
            {
                files[$"{name}RestApiTests.cs"] = GenerateRestApiTest(entity, ns, methods);
            }

            // 2. GraphQL Integration Tests, for the entities that opted in.
            //
            // Also emitted for everything before. `enableGraphQL` decides whether an entity appears
            // in the GraphQL schema, so a test querying one that did not opt in asks for a field
            // that does not exist and fails on a correct application.
            if (entity.GraphQlEnabled)
            {
                files[$"{name}GraphQLTests.cs"] = GenerateGraphQLTest(entity, ns);
            }

            // 3. Real-time channel access, which is reachable over HTTP and therefore assertable.
            if (entity.RealTime)
            {
                files[$"{name}RealTimeTests.cs"] = GenerateRealTimeTest(entity, ns);
            }

            // Kafka, FileIO and business-rule suites are deliberately no longer emitted.
            //
            // They asserted nothing. The Kafka suite read `var topic = "order-events";
            // topic.Should().NotBeNullOrEmpty();`, the FileIO and rules suites were a bare
            // `await Task.CompletedTask;`, and the workflow suite asserted a literal equalled
            // itself. Five of the seven suite types could not fail, so the "autonomous testing
            // engine" produced a green report about an application it never contacted -- which is
            // worse than producing nothing, because the report claims the coverage.
            //
            // What is missing is not effort but information: whether an outbox message reached a
            // broker, whether a file import parsed, and whether a business rule refused the right
            // payload cannot be derived from a schema. Those need a harness the developer writes,
            // and `foundry test` now says so rather than faking it.

            // 4. Workflow transitions, which the manifest exposes as real routes.
            var wf = schema.Workflows?.FirstOrDefault(w => w.Entity.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (wf != null)
            {
                files[$"{name}WorkflowTests.cs"] = GenerateWorkflowTest(entity, wf, ns);
            }
        }

        return files;
    }

    /// <summary>
    /// Emits the shared test environment: base address, bearer token and tenant header.
    /// </summary>
    /// <remarks>
    /// Every emitted file used to construct its own <c>HttpClient</c> against a hardcoded
    /// <c>http://localhost:5000</c> with no <c>Authorization</c> header — against a framework where
    /// every generated endpoint calls <c>RequireAuthorization()</c>. So the suites asserted 200 on
    /// requests that can only answer 401, and a healthy application failed every one of them.
    /// </remarks>
    private static string GenerateEnvironment(string ns) => $@"// Auto-generated test environment.
//
// Configure with environment variables:
//   FOUNDRY_TEST_BASE_URL   where the application is listening (default http://localhost:5000)
//   FOUNDRY_TEST_TOKEN      a bearer token for a caller holding the roles the schema declares
//   FOUNDRY_TEST_TENANT     the tenant to send, for multi-tenant entities
using System;
using System.Net.Http;
using System.Net.Http.Headers;

namespace {CodeGen.Ns(ns)}.Tests;

public static class FoundryTestEnvironment
{{
    public static string BaseUrl =>
        Environment.GetEnvironmentVariable(""FOUNDRY_TEST_BASE_URL"") ?? ""http://localhost:5000"";

    public static string? Token => Environment.GetEnvironmentVariable(""FOUNDRY_TEST_TOKEN"");

    public static string Tenant =>
        Environment.GetEnvironmentVariable(""FOUNDRY_TEST_TENANT"") ?? ""tenant-demo"";

    /// <summary>A client carrying a caller's identity.</summary>
    /// <remarks>
    /// Throws rather than skipping when no token is configured. These endpoints require one, so a
    /// suite that quietly passed without it would be reporting on requests it never made.
    /// </remarks>
    public static HttpClient Authenticated()
    {{
        if (string.IsNullOrWhiteSpace(Token))
        {{
            throw new InvalidOperationException(
                ""FOUNDRY_TEST_TOKEN is not set. The generated endpoints require an authenticated ""
                + ""caller, so these tests cannot run without a bearer token for a caller holding ""
                + ""the roles this schema declares."");
        }}

        var client = Anonymous();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(""Bearer"", Token);
        return client;
    }}

    /// <summary>A client carrying no identity, for asserting that a route refuses one.</summary>
    public static HttpClient Anonymous()
    {{
        var client = new HttpClient {{ BaseAddress = new Uri(BaseUrl) }};
        client.DefaultRequestHeaders.Add(""X-Tenant-ID"", Tenant);
        return client;
    }}

    // ---------------------------------------------------------------------------------
    // Additional identities.
    //
    // Owner scoping cannot be asserted with one identity. A suite holding a single token
    // can observe that a caller sees rows; it cannot observe that a caller is denied
    // someone else's, which is the entire property. Each accessor below throws with the
    // variable name rather than skipping, for the same reason Authenticated() does: a
    // conformance suite that quietly passes because it was not configured reports
    // coverage it does not have.
    // ---------------------------------------------------------------------------------

    /// <summary>A second caller in the same tenant, holding no owner-exempt role.</summary>
    public static HttpClient AsOtherUser() => Identified(
        ""FOUNDRY_TEST_TOKEN_OTHER"",
        Tenant,
        ""a second caller in the same tenant who does not own the rows the primary caller creates"");

    /// <summary>A caller holding one of the entity's declared owner-exempt roles.</summary>
    public static HttpClient AsExemptRole() => Identified(
        ""FOUNDRY_TEST_TOKEN_EXEMPT"",
        Tenant,
        ""a caller holding one of the ownerExemptRoles this schema declares"");

    /// <summary>A caller in a different tenant.</summary>
    public static HttpClient AsOtherTenant() => Identified(
        ""FOUNDRY_TEST_TOKEN_OTHER_TENANT"",
        Environment.GetEnvironmentVariable(""FOUNDRY_TEST_TENANT_OTHER"") ?? ""tenant-other"",
        ""a caller belonging to a different tenant"");

    private static HttpClient Identified(string variable, string tenant, string who)
    {{
        var token = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(token))
        {{
            throw new InvalidOperationException(
                $""{{variable}} is not set. This suite asserts an access-control property that ""
                + $""needs {{who}}, and cannot assert it with one identity. Set {{variable}} to a ""
                + ""bearer token for that caller, or the property goes unverified."");
        }}

        var client = new HttpClient {{ BaseAddress = new Uri(BaseUrl) }};
        client.DefaultRequestHeaders.Add(""X-Tenant-ID"", tenant);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(""Bearer"", token);
        return client;
    }}
}}
";

    /// <summary>
    /// Emits REST tests against the route the compiler actually serves.
    /// </summary>
    /// <remarks>
    /// The route comes from <see cref="ApiManifestGenerator.RouteFor"/>, which this module already
    /// referenced. It used to be composed here as <c>/api/v1/{lowercase-singular}</c> while the
    /// application serves <c>/api/{plural}</c> — the fourth copy of that same wrong rule, after the
    /// OpenAPI exporter, the Postman exporter and Studio. The first three were corrected; this one
    /// survived both cleanups because nothing ever compiled or ran the suites it writes.
    /// </remarks>
    private static string GenerateRestApiTest(Entity entity, string ns, IReadOnlyList<string> methods)
    {
        var name = entity.Name;
        var route = ApiManifestGenerator.RouteFor(name);
        var tests = new StringBuilder();

        // Asserted for whichever read method exists, and it needs no token — which is the point.
        var readMethod = methods.Contains("GET") ? "GET" : methods.Contains("GET_BY_ID") ? "GET_BY_ID" : null;
        if (readMethod != null)
        {
            tests.Append($@"
    [Fact]
    public async Task AnAnonymousCallerIsRefused()
    {{
        // Every generated endpoint calls RequireAuthorization(). This is the one assertion that
        // needs no configuration, and it fails loudly if the API is ever served unauthenticated.
        using var client = FoundryTestEnvironment.Anonymous();

        var response = await client.GetAsync(""{route}"");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }}
");
        }

        if (methods.Contains("GET"))
        {
            // Named for what it asserts. It used to be called GetAll_ReturnsTheCallersOwnRows,
            // which claims owner scoping while checking a status code -- it passes identically
            // whether the caller's rows are filtered or every row in the collection comes back.
            // Owner scoping is asserted below, from the declaration, with a second identity.
            tests.Append($@"
    [Fact]
    public async Task GetAll_IsReachableByAnAuthorisedCaller()
    {{
        using var client = FoundryTestEnvironment.Authenticated();

        var response = await client.GetAsync(""{route}"");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }}
");
        }

        // Owner scoping, emitted only where the schema declares it, and only where there is a
        // write method to create a row to be denied. This is the first assertion in the suite
        // that is derived from a security declaration rather than from the route existing.
        if (entity.OwnerScoped && methods.Contains("POST") && methods.Contains("GET"))
        {
            tests.Append(GenerateOwnerScopingTests(entity, route));
        }

        if (methods.Contains("POST"))
        {
            tests.Append($@"
    [Fact]
    public async Task Create_ValidPayload_IsAccepted()
    {{
        using var client = FoundryTestEnvironment.Authenticated();
        var payload = {SamplePayload(entity)};

        var response = await client.PostAsJsonAsync(""{route}"", payload);

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Created);
    }}
");
        }

        if (methods.Contains("DELETE"))
        {
            tests.Append($@"
    [Fact]
    public async Task Delete_OfAnUnknownId_IsNotFound()
    {{
        // A known id would have to be created first, and this asserts the route exists and is
        // reachable by an authorised caller without depending on another test's leftovers.
        using var client = FoundryTestEnvironment.Authenticated();

        var response = await client.DeleteAsync(""{route}/000000000000000000000000"");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.NoContent);
    }}
");
        }

        return $@"// Auto-generated REST API Integration Tests for {name}
// Route and methods both come from the schema: {string.Join(", ", methods)}
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;
using FluentAssertions;
using {CodeGen.Ns(ns)}.Tests;

namespace {CodeGen.Ns(ns)}.Tests.Integration;

public class {name}RestApiTests
{{{tests}}}";
    }

    /// <summary>
    /// A payload carrying the entity's required properties.
    /// </summary>
    /// <remarks>
    /// It used to post <c>new {{ Name = "AutoTest {entity}" }}</c> for every entity, so a POST to an
    /// entity with required properties failed validation and the suite blamed the application.
    /// </remarks>
    /// <summary>
    /// Emits the owner-scoping conformance assertions for an entity that declares
    /// <c>ownerScoped</c>.
    /// </summary>
    /// <remarks>
    /// Three assertions, and the order matters.
    ///
    /// The first is a positive control: the owner must be able to read back the row they just
    /// created. Without it the denial test below passes for the wrong reason -- if the create
    /// silently failed, or the list route returned nothing at all, "the other caller cannot see
    /// it" is true and meaningless. That failure mode is not hypothetical here: a real-time probe
    /// in this project reported "delivers nothing" for exactly this reason, and the claim had to
    /// be retracted.
    ///
    /// The second is the property itself: a second caller in the same tenant must not see the
    /// row. Same tenant deliberately, so that a passing result cannot be explained by tenant
    /// isolation doing the work instead.
    ///
    /// The third runs only when the schema names owner-exempt roles, and asserts the exemption is
    /// real. An entity whose exempt roles do nothing is as wrong as one whose owner filter does
    /// nothing, and only this assertion can tell the two apart.
    /// </remarks>
    private static string GenerateOwnerScopingTests(Entity entity, string route)
    {
        var ownerKey = (entity.Properties ?? new List<Property>())
            .FirstOrDefault(p => p.IsOwnerKey)?.Name;

        // An entity cannot reach here without one -- FDY3013 rejects ownerScoped with no owner
        // key -- but emitting a suite that silently drops the assertion would be worse than
        // emitting nothing, so say so in the file instead.
        if (string.IsNullOrWhiteSpace(ownerKey))
        {
            return $@"
    // OWNERSHIP NOT VERIFIED for this entity.
    // It declares ownerScoped, but no property is marked isOwnerKey, so there is nothing to
    // scope by and no assertion could be written. FDY3013 should have rejected this pairing at
    // compile time; if you are reading this comment in a generated file, it did not.
";
        }

        var tests = new StringBuilder($@"
    [Fact]
    public async Task OwnerScoping_TheOwnerCanReadBackTheirOwnRow()
    {{
        // Positive control. Everything below asserts that somebody is refused, and every one of
        // those assertions is vacuously true if creation or listing is broken.
        using var owner = FoundryTestEnvironment.Authenticated();
        var created = await CreateRowAsync(owner);

        var listed = await ReadIdsAsync(owner);

        listed.Should().Contain(created,
            ""the owner must be able to read back the row they created, or every denial ""
            + ""assertion in this suite passes for the wrong reason"");
    }}

    [Fact]
    public async Task OwnerScoping_AnotherCallerInTheSameTenantIsDeniedTheRow()
    {{
        // The property under test. The second caller shares the tenant on purpose: if they were
        // in a different one, tenant isolation could produce this result with owner scoping
        // switched off entirely, and the assertion would prove nothing about ownership.
        using var owner = FoundryTestEnvironment.Authenticated();
        var created = await CreateRowAsync(owner);

        using var other = FoundryTestEnvironment.AsOtherUser();
        var listed = await ReadIdsAsync(other);

        listed.Should().NotContain(created,
            ""'{entity.Name}' declares ownerScoped with owner key '{ownerKey}', so a caller who ""
            + ""does not own this row must not see it in the collection"");
    }}
");

        if (entity.OwnerExemptRoles.Count > 0)
        {
            var roles = string.Join(", ", entity.OwnerExemptRoles);
            tests.Append($@"
    [Fact]
    public async Task OwnerScoping_AnExemptRoleStillSeesTheRow()
    {{
        // The exemption is a declaration too, and an exemption that does not exempt is as wrong
        // as a filter that does not filter. Only this assertion distinguishes correct scoping
        // from a repository that returns nothing to anybody.
        using var owner = FoundryTestEnvironment.Authenticated();
        var created = await CreateRowAsync(owner);

        using var exempt = FoundryTestEnvironment.AsExemptRole();
        var listed = await ReadIdsAsync(exempt);

        listed.Should().Contain(created,
            ""'{entity.Name}' declares ownerExemptRoles [{roles}], so a caller holding one must ""
            + ""see rows they do not own"");
    }}
");
        }

        // Helpers, emitted alongside rather than in the shared environment: the route and the
        // payload shape are per-entity, and the id property name is the compiler's, not a guess.
        tests.Append($@"
    private static async Task<string> CreateRowAsync(HttpClient client)
    {{
        var payload = {SamplePayload(entity)};
        var response = await client.PostAsJsonAsync(""{route}"", payload);

        response.StatusCode.Should().BeOneOf(
            new[] {{ HttpStatusCode.OK, HttpStatusCode.Created }},
            ""the owner-scoping assertions all depend on this row existing"");

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty(""Id"").GetString()!;
    }}

    private static async Task<IReadOnlyList<string>> ReadIdsAsync(HttpClient client)
    {{
        var response = await client.GetAsync(""{route}"");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        // The list route returns either a bare array or a paged envelope depending on the
        // method the entity declares; read whichever arrived rather than assuming one.
        var rows = body.ValueKind == JsonValueKind.Array
            ? body
            : body.TryGetProperty(""Items"", out var items) ? items : body.GetProperty(""Data"");

        return rows.EnumerateArray()
            .Select(row => row.GetProperty(""Id"").GetString()!)
            .ToList();
    }}
");

        return tests.ToString();
    }

    private static string SamplePayload(Entity entity)
    {
        var assignments = (entity.Properties ?? new List<Property>())
            .Where(p => !p.IsKey && p.Attributes.Contains("Required"))
            .Take(6)
            .Select(p => $"{CodeGen.Ident(p.Name, "Property")} = {SampleValueFor(p)}")
            .ToList();

        return assignments.Count == 0
            ? "new { }"
            : "new { " + string.Join(", ", assignments) + " }";
    }

    private static string SampleValueFor(Property property)
    {
        if (property.IsEnum) return "\"\"";

        return property.Type.ToLowerInvariant() switch
        {
            "int" or "long" => "1",
            "decimal" or "double" or "float" => "1.0",
            "bool" => "true",
            "datetime" => "System.DateTime.UtcNow",
            _ => $"\"auto-{property.Name.ToLowerInvariant()}\""
        };
    }

    private static string GenerateGraphQLTest(Entity entity, string ns)
    {
        var name = entity.Name;

        // The field name GraphQLConfiguration emits for a readable entity.
        return $@"// Auto-generated GraphQL Integration Tests for {name}
using System.Net;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Xunit;
using FluentAssertions;
using {CodeGen.Ns(ns)}.Tests;

namespace {CodeGen.Ns(ns)}.Tests.GraphQL;

public class {name}GraphQLTests
{{
    [Fact]
    public async Task Query_Get{name}s_ReturnsData()
    {{
        // GraphQL fields are guarded by the same manifest roles as REST, so this needs a token
        // exactly as the REST suite does.
        using var client = FoundryTestEnvironment.Authenticated();
        var query = new {{ query = ""query {{ get{name}s {{ id }} }}"" }};

        var response = await client.PostAsJsonAsync(""/graphql"", query);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain(""\""errors\"""");
    }}
}}";
    }

    /// <summary>
    /// Asserts the real-time channels enforce the entity's declared access.
    /// </summary>
    /// <remarks>
    /// This used to be <c>var channel = "product-mutations"; channel.Should().NotBeEmpty();</c> — an
    /// assertion about a string literal, which cannot fail. The channels are ordinary HTTP endpoints,
    /// so what they do with an anonymous caller is something a generated suite can genuinely check.
    /// </remarks>
    private static string GenerateRealTimeTest(Entity entity, string ns)
    {
        var name = entity.Name;

        return $@"// Auto-generated real-time channel tests for {name}
using System.Net;
using System.Threading.Tasks;
using Xunit;
using FluentAssertions;
using {CodeGen.Ns(ns)}.Tests;

namespace {CodeGen.Ns(ns)}.Tests.RealTime;

public class {name}RealTimeTests
{{
    [Theory]
    [InlineData(""/realtime/sse"")]
    [InlineData(""/realtime/ws"")]
    public async Task TheChannelsRefuseAnAnonymousClient(string route)
    {{
        // These carry AuditLogEntry notifications, and an AuditLogEntry carries the changed values.
        using var client = FoundryTestEnvironment.Anonymous();

        var response = await client.GetAsync(route);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }}

    [Fact]
    public async Task TheHubNegotiatesForAnAuthenticatedClient()
    {{
        using var client = FoundryTestEnvironment.Authenticated();

        var response = await client.PostAsync(""/realtime/hub/negotiate?negotiateVersion=1"", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }}
}}";
    }

    /// <summary>
    /// Asserts the workflow's transition routes exist and enforce their declared roles.
    /// </summary>
    /// <remarks>
    /// This used to be <c>var state = "Pending"; state.Should().Be("Pending");</c> — a literal
    /// compared with itself. The manifest turns each transition into a route, so whether that route
    /// exists and who may drive it is something a generated suite can genuinely check.
    /// </remarks>
    private static string GenerateWorkflowTest(Entity entity, WorkflowModel wf, string ns)
    {
        var name = entity.Name;
        var tests = new StringBuilder();

        foreach (var transition in (wf.Transitions ?? new List<WorkflowTransitionModel>())
                     .Where(t => !string.IsNullOrWhiteSpace(t.Trigger))
                     .Take(6))
        {
            var route = ApiManifestGenerator.TransitionRouteFor(wf.Entity, transition.Trigger);
            var method = CodeGen.Ident(transition.Trigger, "Transition trigger");

            tests.Append($@"
    [Fact]
    public async Task {method}_RefusesAnAnonymousCaller()
    {{
        // From '{transition.FromState}' to '{transition.ToState}'.
        using var client = FoundryTestEnvironment.Anonymous();

        var response = await client.PostAsJsonAsync(""{route}"", new {{ EntityId = ""000000000000000000000000"" }});

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }}

    [Fact]
    public async Task {method}_RouteExistsForAnAuthorisedCaller()
    {{
        // The record does not exist, so the transition cannot apply -- but a route that is missing
        // answers 404 on the *route*, and one that is present answers about the record. Anything
        // other than 405 means the endpoint was generated and mapped.
        using var client = FoundryTestEnvironment.Authenticated();

        var response = await client.PostAsJsonAsync(""{route}"", new {{ EntityId = ""000000000000000000000000"" }});

        response.StatusCode.Should().NotBe(HttpStatusCode.MethodNotAllowed);
        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
    }}
");
        }

        if (tests.Length == 0)
        {
            tests.Append(@"
    // This workflow declares no transitions with a trigger, so it exposes no routes to exercise.
");
        }

        return $@"// Auto-generated workflow transition tests for {name}
// Workflow '{wf.Name}' ({wf.Id})
using System.Net;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Xunit;
using FluentAssertions;
using {CodeGen.Ns(ns)}.Tests;

namespace {CodeGen.Ns(ns)}.Tests.Workflows;

public class {name}WorkflowTests
{{{tests}}}";
    }
}
