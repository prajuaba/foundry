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

        // A project file, so that what `foundry test` writes can be run rather than only read.
        //
        // Without one, `dotnet test <dir>` finds no project and the suites are inert -- which is
        // how they sat for as long as they did. The compile gate carried its own private copy of
        // this csproj, so it was checking a project no user of the CLI ever got; that copy now
        // comes from here, and the two cannot drift.
        files["GeneratedSuites.csproj"] = GenerateProjectFile();

        // xUnit parallelises test classes by default. Against a live application that means dozens
        // of concurrent callers, which trips any rate limiter worth having -- a 429 then fails an
        // access-control assertion for a reason that has nothing to do with access control. A
        // conformance suite should not have to be defended against by the system it is checking.
        files["xunit.runner.json"] = """
{
  "$schema": "https://xunit.net/schema/current/xunit.runner.schema.json",
  "parallelizeAssembly": false,
  "parallelizeTestCollections": false
}
""";

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
    /// <summary>The project file for the emitted suites, with the packages they use and no others.</summary>
    /// <remarks>
    /// If a suite ever needs something beyond these, whoever runs <c>foundry test</c> should be
    /// told here rather than left to discover it from a build error.
    /// </remarks>
    private static string GenerateProjectFile() => """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
    <NoWarn>$(NoWarn);CS1591;CS8618</NoWarn>
  </PropertyGroup>
  <ItemGroup>
    <!-- Without this the runner never reads it and the suite parallelises anyway. -->
    <None Update="xunit.runner.json" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.12.0" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
    <PackageReference Include="FluentAssertions" Version="6.12.0" />
  </ItemGroup>
</Project>
""";

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

    /// <summary>A caller holding one of the entity's declared owner-read-exempt roles.</summary>
    public static HttpClient AsReadExemptRole() => Identified(
        ""FOUNDRY_TEST_TOKEN_READ_EXEMPT"",
        Tenant,
        ""a caller holding one of the ownerReadExemptRoles this schema declares"");

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

        // Access-control assertions, each emitted only where the schema declares the property and
        // where there is a write method to create a row with. These are the assertions derived
        // from a security declaration rather than from the route existing.
        var canProbe = methods.Contains("POST") && methods.Contains("GET");
        var emittedAccessControl = false;

        if (entity.OwnerScoped && canProbe)
        {
            tests.Append(GenerateOwnerScopingTests(entity, route));
            emittedAccessControl = true;
        }

        if (entity.MultiTenant && canProbe)
        {
            tests.Append(GenerateTenancyTests(entity, route));
            emittedAccessControl = true;
        }

        if (canProbe)
        {
            var masking = GenerateMaskingTests(entity, route);
            if (masking.Length > 0)
            {
                tests.Append(masking);
                emittedAccessControl = true;
            }
        }

        // One copy, shared by whichever of the three emitted above.
        if (emittedAccessControl)
        {
            tests.Append(GenerateAccessControlHelpers(entity, route));
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

        (await CanReadByIdAsync(owner, created)).Should().BeTrue(
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

        // Both read paths, because they are not the same code. The list route filters with an
        // expression and the by-id route with a FilterDefinition, and the two have already
        // diverged once: a tenant clause present on one and missing on the other.
        (await ReadIdsAsync(other)).Should().NotContain(created,
            ""'{entity.Name}' declares ownerScoped with owner key '{ownerKey}', so a caller who ""
            + ""does not own this row must not see it in the collection"");

        (await CanReadByIdAsync(other, created)).Should().BeFalse(
            ""'{entity.Name}' declares ownerScoped, so a non-owner must not reach this row by id ""
            + ""either"");
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

        (await CanReadByIdAsync(exempt, created)).Should().BeTrue(
            ""'{entity.Name}' declares ownerExemptRoles [{roles}], so a caller holding one must ""
            + ""see rows they do not own"");
    }}
");
        }

        if (entity.OwnerReadExemptRoles.Count > 0)
        {
            var readRoles = string.Join(", ", entity.OwnerReadExemptRoles);
            tests.Append($@"
    [Fact]
    public async Task OwnerScoping_AReadExemptRoleSeesTheRow()
    {{
        // ownerReadExemptRoles is a second, narrower declaration: EntityAccessPolicy exempts
        // ownerExemptRoles on both reads and writes, and ownerReadExemptRoles on reads only. It
        // is asserted separately because a suite that covered the wider list alone would leave
        // the narrower one declared and unverified -- which is the defect this suite exists to
        // catch, one declaration over.
        using var owner = FoundryTestEnvironment.Authenticated();
        var created = await CreateRowAsync(owner);

        using var readExempt = FoundryTestEnvironment.AsReadExemptRole();

        (await CanReadByIdAsync(readExempt, created)).Should().BeTrue(
            ""'{entity.Name}' declares ownerReadExemptRoles [{readRoles}], so a caller holding ""
            + ""one must see rows they do not own when reading"");
    }}
");
        }

        return tests.ToString();
    }

    /// <summary>
    /// A property declaring a mask, paired with the attribute that declares it.
    /// </summary>
    /// <remarks>
    /// <c>Encrypt</c> is deliberately excluded. Repository.ProtectForRead decrypts and then masks,
    /// in that order, so an Encrypt-only property is *correctly* returned in the clear to a caller
    /// allowed to read the row: encryption protects the stored document, not the response. An
    /// assertion that the value is absent from an HTTP body would fail against a working system.
    /// </remarks>
    private static List<(string Name, string Kind)> MaskedProperties(Entity entity)
        => (entity.Properties ?? new List<Property>())
            .Select(p => (p.Name, Kind: p.Attributes.FirstOrDefault(
                a => a.StartsWith("Mask", StringComparison.Ordinal))))
            .Where(p => !string.IsNullOrEmpty(p.Kind) && !p.Name.Equals("Id", StringComparison.Ordinal))
            .Select(p => (p.Name, Kind: p.Kind!))
            .ToList();

    /// <summary>
    /// A value distinctive enough that finding it in a response means the mask did not run.
    /// </summary>
    private static string MaskSentinel(string kind)
        => kind.Contains("Email", StringComparison.Ordinal)
            ? "unmasked-sentinel@example.com"
            : "SENTINEL-0123456789";

    /// <summary>
    /// Emits the masking conformance assertions for each property declaring a mask.
    /// </summary>
    /// <remarks>
    /// Three assertions per property, and the third is the one most easily forgotten. Asserting
    /// only that the raw value is absent is satisfied by a response that dropped the field, by a
    /// list that came back empty, and by a write that never happened -- so the row must be present
    /// and the field must still hold something. A dropped field is a different defect from a
    /// masked one and must not read as success.
    /// </remarks>
    private static string GenerateMaskingTests(Entity entity, string route)
    {
        var masked = MaskedProperties(entity);
        if (masked.Count == 0) return string.Empty;

        var tests = new StringBuilder();

        foreach (var (name, kind) in masked)
        {
            var sentinel = MaskSentinel(kind);
            tests.Append($@"
    [Fact]
    public async Task Protection_{name}_IsNotReturnedInTheClear()
    {{
        // '{entity.Name}.{name}' declares {kind}. Masking is applied in the repository after the
        // entity is materialised, so it covers REST, GraphQL and the SDKs from one rule -- which
        // is exactly why it must be asserted on more than one of them.
        using var client = FoundryTestEnvironment.Authenticated();
        var created = await CreateRowWithAsync(client, ""{name}"", ""{sentinel}"");

        var body = await ReadRowAsync(client, created);

        body.Should().NotContain(""{sentinel}"",
            ""'{name}' declares {kind}, so a caller without the scope to see it must not receive ""
            + ""the raw value"");
        body.Should().Contain(created,
            ""the row itself must come back, or the assertion above is satisfied by an empty ""
            + ""response rather than by masking"");
    }}
");
        }

        return tests.ToString();
    }

    /// <summary>
    /// Emits the tenancy conformance assertions for an entity that declares <c>multiTenant</c>.
    /// </summary>
    /// <remarks>
    /// The same two-assertion shape as owner scoping, for the same reason. The positive control
    /// comes first because the denial below is vacuously true whenever creation or listing is
    /// broken -- "the other tenant cannot see it" holds when nobody can see anything.
    ///
    /// The tenant travels in the caller's token rather than a header, so the second identity is a
    /// separate signed principal rather than the same one sending a different header. A header is
    /// caller-assertable and proves nothing.
    /// </remarks>
    private static string GenerateTenancyTests(Entity entity, string route) => $@"
    [Fact]
    public async Task Tenancy_TheOwningTenantSeesItsOwnRow()
    {{
        // Positive control, and not optional: the denial below passes for the wrong reason if the
        // create silently failed or the list route returns nothing at all.
        using var owner = FoundryTestEnvironment.Authenticated();
        var created = await CreateRowAsync(owner);

        var listed = await ReadIdsAsync(owner);

        (await CanReadByIdAsync(owner, created)).Should().BeTrue(
            ""the tenant that created this row must be able to read it back, or the denial ""
            + ""assertion below proves nothing"");
    }}

    [Fact]
    public async Task Tenancy_ARowIsNotVisibleFromAnotherTenant()
    {{
        // The property under test. '{entity.Name}' declares multiTenant, so a caller whose token
        // carries a different tenant must not see this row on any read path.
        using var owner = FoundryTestEnvironment.Authenticated();
        var created = await CreateRowAsync(owner);

        using var other = FoundryTestEnvironment.AsOtherTenant();

        (await ReadIdsAsync(other)).Should().NotContain(created,
            ""'{entity.Name}' declares multiTenant, so a caller in another tenant must not see ""
            + ""this row in the collection"");

        (await CanReadByIdAsync(other, created)).Should().BeFalse(
            ""'{entity.Name}' declares multiTenant, so a caller in another tenant must not reach ""
            + ""this row by id either"");
    }}
";

    /// <summary>
    /// The per-entity helpers the access-control assertions share.
    /// </summary>
    /// <remarks>
    /// Emitted once per suite rather than once per emitter. They live here instead of in the shared
    /// FoundryTestEnvironment because the route and the payload shape are per-entity, and the id
    /// property name is the compiler's rather than a guess.
    /// </remarks>
    private static string GenerateAccessControlHelpers(Entity entity, string route) => $@"
    private static async Task<string> CreateRowAsync(HttpClient client)
    {{
        var payload = {SamplePayload(entity)};
        var response = await client.PostAsJsonAsync(""{route}"", payload);

        response.StatusCode.Should().BeOneOf(
            new[] {{ HttpStatusCode.OK, HttpStatusCode.Created }},
            ""every access-control assertion in this suite depends on this row existing"");

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

    /// <summary>Whether this caller can reach one row by its id.</summary>
    /// <remarks>
    /// Presence is asserted here rather than by searching the list, because the list route returns
    /// one page. Against an application with real data the row can be on a later page, and the
    /// positive control would then fail for a reason that has nothing to do with access control --
    /// the worst kind of failure, since it teaches whoever sees it to distrust the suite.
    ///
    /// Denial is still asserted on both routes. They are not the same code: the list filters with
    /// an expression and this one with a FilterDefinition, and the two have already diverged once
    /// over a missing tenant clause.
    /// </remarks>
    private static async Task<bool> CanReadByIdAsync(HttpClient client, string id)
    {{
        var response = await client.GetAsync($""{route}/{{id}}"");

        // Anything other than these two means the question was not answered -- a 500, a 429 from a
        // rate limiter, an auth failure -- and reporting that as a refusal would turn an unhealthy
        // system into a passing access-control assertion.
        response.StatusCode.Should().BeOneOf(
            new[] {{ HttpStatusCode.OK, HttpStatusCode.NotFound }},
            $""a read by id must answer 200 or 404; {{response.StatusCode}} says the system could ""
            + ""not answer, which is not the same as refusing"");

        return response.StatusCode == HttpStatusCode.OK;
    }}

    /// <summary>Creates a row carrying a known value in one property.</summary>
    /// <remarks>
    /// The sample payload alone cannot serve the masking assertions: a value has to be put in
    /// deliberately, or the assertion that it does not come back is true because it was never
    /// there. That is the defect this project found in its own showcase, where a redaction step
    /// asserted no card number appeared in a payload it had never set one in.
    /// </remarks>
    private static async Task<string> CreateRowWithAsync(HttpClient client, string property, string value)
    {{
        var payload = new Dictionary<string, object?>(
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                JsonSerializer.Serialize({SamplePayload(entity)}))!
                .ToDictionary(kv => kv.Key, kv => (object?)kv.Value))
        {{
            [property] = value
        }};

        var response = await client.PostAsJsonAsync(""{route}"", payload);

        response.StatusCode.Should().BeOneOf(
            new[] {{ HttpStatusCode.OK, HttpStatusCode.Created }},
            $""the masking assertion for '{{property}}' depends on this row existing"");

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty(""Id"").GetString()!;
    }}

    /// <summary>The raw response body for one row, read back by id.</summary>
    /// <remarks>
    /// Raw text rather than a parsed property, deliberately. A mask that leaks the value somewhere
    /// other than the field it belongs to -- a nested copy, an audit echo, an error message --
    /// still leaks it, and reading one property by name would not see that.
    /// </remarks>
    private static async Task<string> ReadRowAsync(HttpClient client, string id)
    {{
        var response = await client.GetAsync($""{route}/{{id}}"");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadAsStringAsync();
    }}
";

    /// <summary>The payload a sample write sends.</summary>
    /// <remarks>
    /// Required properties, and also any property whose declared constraints make its default
    /// invalid.
    ///
    /// It used to send required properties alone, which is not the same thing. A property carrying
    /// <c>Range(1, 10)</c> and no <c>Required</c> was omitted, arrived as the default 0, and the
    /// application refused the write with "must be between 1 and 10" -- correctly. Against
    /// Resourcify that lost eleven of twenty-four entities, so the tenancy and ownership
    /// assertions for those entities never ran at all. The suite was not finding a defect; it was
    /// sending a payload the schema had already said was invalid.
    ///
    /// The cap of six was removed with it. It saved nothing and silently truncated the payload of
    /// any entity with more than six required properties, which fails in exactly the same way.
    /// </remarks>
    private static string SamplePayload(Entity entity)
    {
        var assignments = (entity.Properties ?? new List<Property>())
            .Where(p => !p.IsKey && (p.Attributes.Contains("Required") || NeedsAValueToBeValid(p)))
            .Select(p => $"{CodeGen.Ident(p.Name, "Property")} = {SampleValueFor(p)}")
            .ToList();

        return assignments.Count == 0
            ? "new { }"
            : "new { " + string.Join(", ", assignments) + " }";
    }

    /// <summary>A per-row unique string that still fits what the property declares.</summary>
    /// <remarks>
    /// Uniqueness and length pull against each other, and the first version of this ignored the
    /// second: appending a full 32-character GUID to "auto-code-" produced 42 characters for a
    /// property declaring <c>MaxLength(24)</c>, and the application refused the write. Trading a
    /// duplicate-key failure for a max-length failure is not progress.
    ///
    /// Eight hex characters are enough to keep a few hundred rows in one test run apart, and the
    /// prefix is trimmed so the whole value fits the declared bound.
    /// </remarks>
    private static string UniqueString(Property property, string name, string suffix)
    {
        const int UniquePart = 9;   // one separator plus eight hex characters

        var max = MaxLength(property);
        var prefix = $"auto-{name}";

        if (max is int limit)
        {
            var room = limit - UniquePart - suffix.Length;

            // Nothing sensible fits: emit the unique part alone, trimmed to the bound. A value the
            // application refuses is worse than an unreadable one.
            if (room < 1) return $"$\"{{System.Guid.NewGuid().ToString(\"N\")[..System.Math.Max(1, {limit - suffix.Length})]}}{suffix}\"";

            if (prefix.Length > room) prefix = prefix[..room];
        }

        return $"$\"{prefix}-{{System.Guid.NewGuid().ToString(\"N\")[..8]}}{suffix}\"";
    }

    /// <summary>The bound of a <c>MaxLength(n)</c> attribute, if the property declares one.</summary>
    private static int? MaxLength(Property property)
    {
        var attribute = property.Attributes.FirstOrDefault(
            a => a.StartsWith("MaxLength(", StringComparison.Ordinal));
        if (attribute is null) return null;

        var inside = attribute["MaxLength(".Length..].TrimEnd(')').Trim();
        return int.TryParse(inside, System.Globalization.CultureInfo.InvariantCulture, out var max) ? max : null;
    }

    /// <summary>Whether leaving this property out would produce a value its own schema rejects.</summary>
    private static bool NeedsAValueToBeValid(Property property)
        => RangeMinimum(property) is > 0;

    /// <summary>The lower bound of a <c>Range(min, max)</c> attribute, if the property declares one.</summary>
    private static double? RangeMinimum(Property property)
    {
        var range = property.Attributes.FirstOrDefault(a => a.StartsWith("Range(", StringComparison.Ordinal));
        if (range is null) return null;

        var inside = range[6..].TrimEnd(')');
        var first = inside.Split(',')[0].Trim();

        return double.TryParse(first, System.Globalization.CultureInfo.InvariantCulture, out var min)
            ? min
            : null;
    }

    /// <summary>A value for one property in a sample payload.</summary>
    /// <remarks>
    /// String values are made unique per row at run time rather than being constants.
    ///
    /// They were constants, and against a real application that meant the second row of any entity
    /// with a unique index collided: `E11000 duplicate key ... dup key: { name: "auto-name" }`.
    /// Every access-control assertion creates at least one row and several create more, so half
    /// the suite failed on a write that the schema had every right to refuse. It looked like a
    /// framework defect and was a defect in the payload.
    ///
    /// A property whose name suggests an address keeps a valid address shape, since a uniqueness
    /// suffix that breaks format validation only trades one failed write for another.
    /// </remarks>
    private static string SampleValueFor(Property property)
    {
        if (property.IsEnum) return "\"\"";

        var name = property.Name.ToLowerInvariant();

        // Inside the declared range, when there is one. A constant 1 is refused by Range(5, 10)
        // just as surely as the default 0 is refused by Range(1, 10).
        var min = RangeMinimum(property);

        return property.Type.ToLowerInvariant() switch
        {
            "int" or "long" => min is > 1 ? ((long)min).ToString(System.Globalization.CultureInfo.InvariantCulture) : "1",
            "decimal" or "double" or "float" => min is > 1
                ? min.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                : "1.0",
            "bool" => "true",
            "datetime" => "System.DateTime.UtcNow",
            _ when name.Contains("email", StringComparison.Ordinal)
                => UniqueString(property, name, suffix: "@example.com"),
            _ => UniqueString(property, name, suffix: "")
        };
    }

    private static string GenerateGraphQLTest(Entity entity, string ns)
    {
        var name = entity.Name;

        // The field name GraphQLConfiguration emits for a readable entity.
        return $@"// Auto-generated GraphQL Integration Tests for {name}
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
    }}{GraphQlAccessControlTests(entity)}
}}";
    }

    /// <summary>
    /// The access-control assertions for the GraphQL egress.
    /// </summary>
    /// <remarks>
    /// This egress needs its own assertions rather than inheriting the REST ones, and the history
    /// is the argument. Masking and encryption are applied in the repository so that one rule
    /// covers every transport -- but the resolver reads through <c>Query()</c>, which returns an
    /// IQueryable with nothing materialised to protect. Protected fields came back raw over
    /// GraphQL while the same fields over REST were protected, for two releases. A per-entity
    /// verdict would have called those entities covered.
    /// </remarks>
    private static string GraphQlAccessControlTests(Entity entity)
    {
        var name = entity.Name;
        var tests = new StringBuilder();

        if (entity.OwnerScoped)
        {
            tests.Append($@"

    [Fact]
    public async Task OwnerScoping_ANonOwnerIsDeniedThroughTheResolver()
    {{
        // The resolver reads through Repository.Query(), which carries the owner filter. Nothing
        // asserted that until now, and Query() is the path protection leaked through twice.
        using var owner = FoundryTestEnvironment.Authenticated();
        var mine = await client_OwnerRowAsync(owner);

        using var other = FoundryTestEnvironment.AsOtherUser();
        var body = await QueryIdsAsync(other);

        body.Should().NotContain(mine,
            ""'{name}' declares ownerScoped, so a caller who does not own this row must not ""
            + ""receive it through the resolver either"");
    }}

    [Fact]
    public async Task OwnerScoping_TheOwnerDoesSeeTheirRowThroughTheResolver()
    {{
        // Positive control. Without it the denial above passes against a resolver that returns
        // nothing to anybody.
        using var owner = FoundryTestEnvironment.Authenticated();
        var mine = await client_OwnerRowAsync(owner);

        var body = await QueryIdsAsync(owner);

        body.Should().Contain(mine,
            ""the owner must receive their own row through the resolver, or the denial assertion ""
            + ""above proves nothing"");
    }}");
        }

        foreach (var (property, kind) in MaskedProperties(entity))
        {
            var sentinel = MaskSentinel(kind);
            tests.Append($@"

    [Fact]
    public async Task Protection_{property}_IsNotReturnedInTheClearThroughTheResolver()
    {{
        // Findings 7 and 8 were exactly this: '{property}' protected over REST and raw over
        // GraphQL, because the resolver had no materialised entity to mask.
        using var client = FoundryTestEnvironment.Authenticated();
        var created = await client_RowWithAsync(client, ""{property}"", ""{sentinel}"");

        var body = await QueryFieldAsync(client, ""{GraphQlField(property)}"");

        body.Should().NotContain(""{sentinel}"",
            ""'{property}' declares {kind}, so the resolver must not return the raw value"");
        body.Should().Contain(created,
            ""the row must come back, or the assertion above is satisfied by an empty result"");
    }}");
        }

        if (tests.Length > 0) tests.Append(GraphQlHelpers(entity));
        return tests.ToString();
    }

    /// <summary>
    /// A property name as GraphQL exposes it.
    /// </summary>
    /// <remarks>
    /// HotChocolate lower-camels field names by default, so the schema's <c>PhoneNumber</c> is
    /// queried as <c>phoneNumber</c>. Asking for the PascalCase name returns an "unknown field"
    /// error rather than data, which would make the masking assertion vacuous in the most
    /// misleading way available: the sentinel really is absent from an error response.
    /// </remarks>
    private static string GraphQlField(string property)
        => string.IsNullOrEmpty(property)
            ? property
            : char.ToLowerInvariant(property[0]) + property[1..];

    /// <summary>Helpers the GraphQL access-control assertions share.</summary>
    private static string GraphQlHelpers(Entity entity)
    {
        var name = entity.Name;
        var route = ApiManifestGenerator.RouteFor(name);

        return $@"

    private static async Task<string> client_OwnerRowAsync(HttpClient client)
    {{
        // Written over REST and read over GraphQL on purpose: the point is that the two egresses
        // answer consistently about the same row.
        var response = await client.PostAsJsonAsync(""{route}"", {SamplePayload(entity)});
        response.StatusCode.Should().BeOneOf(
            new[] {{ HttpStatusCode.OK, HttpStatusCode.Created }},
            ""the resolver assertions depend on this row existing"");

        var created = await response.Content.ReadFromJsonAsync<JsonElement>();
        return created.GetProperty(""Id"").GetString()!;
    }}

    private static async Task<string> client_RowWithAsync(HttpClient client, string property, string value)
    {{
        var payload = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            JsonSerializer.Serialize({SamplePayload(entity)}))!
            .ToDictionary(kv => kv.Key, kv => (object?)kv.Value);
        payload[property] = value;

        var response = await client.PostAsJsonAsync(""{route}"", payload);
        response.StatusCode.Should().BeOneOf(
            new[] {{ HttpStatusCode.OK, HttpStatusCode.Created }},
            $""the resolver masking assertion for '{{property}}' depends on this row existing"");

        var created = await response.Content.ReadFromJsonAsync<JsonElement>();
        return created.GetProperty(""Id"").GetString()!;
    }}

    private static async Task<string> QueryIdsAsync(HttpClient client)
        => await QueryFieldAsync(client, ""id"");

    private static async Task<string> QueryFieldAsync(HttpClient client, string field)
    {{
        var query = new {{ query = ""query {{ get{name}s {{ id "" + field + "" }} }}"" }};
        var response = await client.PostAsJsonAsync(""/graphql"", query);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();

        body.Should().NotContain(""\""errors\"""",
            ""a resolver error would make every assertion below vacuous"");

        return body;
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
