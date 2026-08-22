using Foundry.Schema.Compiler;
using Foundry.Testing.Generators;
using Xunit;

namespace Foundry.Testing.Tests;

/// <summary>
/// What the generated suites assert, and about which routes.
/// </summary>
/// <remarks>
/// Compiling the output cannot catch either defect this covers. A suite that asks for the wrong URL
/// builds perfectly, and so does one that asserts <c>200 OK</c> on a route that answers 401 — they
/// simply fail at run time against a correct application, and blame it.
/// </remarks>
public class SuiteContentTests
{
    private static SchemaModel Schema(
        string[]? methods = null, bool graphQl = false, string name = "Customer") => new()
    {
        Namespace = "Sales.Domain",
        Entities =
        [
            new Entity
            {
                Name = name,
                GraphQlEnabled = graphQl,
                ApiEnabledMethods = [.. methods ?? ["GET", "POST"]],
                Properties =
                [
                    new Property { Name = "Id", Type = "ObjectId", IsKey = true },
                    new Property { Name = "Email", Type = "string", Attributes = ["Required"] },
                    new Property { Name = "Age", Type = "int" }
                ]
            }
        ]
    };

    private static SchemaModel OwnerScopedSchema(
        string[]? exemptRoles = null, bool withOwnerKey = true, string[]? methods = null) => new()
    {
        Namespace = "Sales.Domain",
        Entities =
        [
            new Entity
            {
                Name = "Customer",
                OwnerScoped = true,
                OwnerExemptRoles = [.. exemptRoles ?? Array.Empty<string>()],
                ApiEnabledMethods = [.. methods ?? ["GET", "POST"]],
                Properties =
                [
                    new Property { Name = "Id", Type = "ObjectId", IsKey = true },
                    new Property { Name = "Email", Type = "string", Attributes = ["Required"] },
                    new Property { Name = "OwnerId", Type = "string", IsOwnerKey = withOwnerKey }
                ]
            }
        ]
    };

    private static Dictionary<string, string> Generate(SchemaModel schema)
        => AutomatedTestSuiteGenerator.GenerateAllTestSuites(schema);

    // ── The route ───────────────────────────────────────────────────────────

    [Fact]
    public void TheRestSuiteUsesTheRouteTheApplicationServes()
    {
        // /api/v1/customer was emitted here while the application serves /api/customers -- the same
        // wrong rule the OpenAPI exporter, the Postman exporter and Studio each had, corrected in all
        // three while this copy survived because nothing ran what it writes.
        var suite = Generate(Schema())["CustomerRestApiTests.cs"];

        Assert.Contains("\"/api/customers\"", suite);
        Assert.DoesNotContain("/api/v1/", suite);
    }

    [Fact]
    public void TheRouteComesFromTheCompilerRatherThanAMatchingCopy()
    {
        // Pins the emitted text to ApiManifestGenerator itself, so a change to the route rule shows
        // up here rather than in a user's failing suite months later.
        var schema = Schema(name: "Category");
        var suite = Generate(schema)["CategoryRestApiTests.cs"];

        Assert.Contains($"\"{ApiManifestGenerator.RouteFor("Category")}\"", suite);
    }

    // ── Authentication ──────────────────────────────────────────────────────

    [Fact]
    public void ARequestExpectingSuccessCarriesACallersToken()
    {
        // Every generated endpoint calls RequireAuthorization(), so an unauthenticated request can
        // only answer 401. Asserting 200 on one made a healthy application look broken.
        var suite = Generate(Schema())["CustomerRestApiTests.cs"];

        Assert.Contains("FoundryTestEnvironment.Authenticated()", suite);
        Assert.Contains("HttpStatusCode.OK", suite);
    }

    [Fact]
    public void TheSuiteAlsoAssertsThatAnonymousAccessIsRefused()
    {
        // The half that needs no configuration, and the one that would catch an API accidentally
        // served without authorisation.
        var suite = Generate(Schema())["CustomerRestApiTests.cs"];

        Assert.Contains("FoundryTestEnvironment.Anonymous()", suite);
        Assert.Contains("HttpStatusCode.Unauthorized", suite);
    }

    [Fact]
    public void TheEnvironmentIsEmittedOnceAndRefusesToRunWithoutAToken()
    {
        var files = Generate(Schema());

        Assert.True(files.ContainsKey("FoundryTestEnvironment.cs"));

        var environment = files["FoundryTestEnvironment.cs"];
        Assert.Contains("FOUNDRY_TEST_BASE_URL", environment);
        Assert.Contains("FOUNDRY_TEST_TOKEN", environment);

        // Fails rather than skips, which is this repository's rule everywhere else: a suite that
        // quietly passes without its subject reports on requests it never made.
        Assert.Contains("throw new InvalidOperationException", environment);
    }

    [Fact]
    public void NoSuiteHardcodesAnAddress()
    {
        // http://localhost:5000 was baked into every emitted file, so the suites could only ever be
        // pointed at one application.
        foreach (var (name, content) in Generate(Schema(graphQl: true)))
        {
            if (name == "FoundryTestEnvironment.cs") continue;
            Assert.DoesNotContain("http://localhost:5000", content);
        }
    }

    // ── What gets emitted at all ────────────────────────────────────────────

    [Fact]
    public void AnEntityWithNoApiSurfaceGetsNoRestSuite()
    {
        // An entity declaring no methods has no REST routes. Testing it asserted 200 against URLs
        // the application does not serve.
        var files = Generate(Schema(methods: []));

        Assert.False(files.ContainsKey("CustomerRestApiTests.cs"));
    }

    [Fact]
    public void OnlyTheDeclaredMethodsAreExercised()
    {
        var suite = Generate(Schema(methods: ["GET"]))["CustomerRestApiTests.cs"];

        Assert.Contains("GetAll_IsReachableByAnAuthorisedCaller", suite);

        // Named for what it asserts. The old name, GetAll_ReturnsTheCallersOwnRows, claimed owner
        // scoping while the body checked a status code -- it read identically whether rows were
        // filtered to the caller or the whole collection came back.
        Assert.DoesNotContain("GetAll_ReturnsTheCallersOwnRows", suite);
        Assert.DoesNotContain("Create_ValidPayload_IsAccepted", suite);
        Assert.DoesNotContain("Delete_", suite);
    }

    [Fact]
    public void AGraphQLSuiteIsWrittenOnlyForAnEntityThatOptedIn()
    {
        // enableGraphQL decides whether the entity appears in the GraphQL schema at all, so querying
        // one that did not opt in asks for a field that does not exist.
        Assert.False(Generate(Schema(graphQl: false)).ContainsKey("CustomerGraphQLTests.cs"));
        Assert.True(Generate(Schema(graphQl: true)).ContainsKey("CustomerGraphQLTests.cs"));
    }

    [Fact]
    public void ThePostPayloadCarriesTheRequiredProperties()
    {
        // It posted `new { Name = "AutoTest Customer" }` for every entity, so a POST to an entity
        // with required properties failed validation and the suite blamed the application.
        var suite = Generate(Schema())["CustomerRestApiTests.cs"];

        Assert.Contains("Email =", suite);
        Assert.DoesNotContain("AutoTest", suite);
    }

    // ── Owner scoping ───────────────────────────────────────────────────────

    [Fact]
    public void AnOwnerScopedEntityGetsAssertionsThatNeedASecondIdentity()
    {
        // The whole point of the emission. Before this existed, the only ownership-flavoured thing
        // in the suite was a test called GetAll_ReturnsTheCallersOwnRows that asserted a status
        // code, which passes with owner scoping switched off.
        var suite = Generate(OwnerScopedSchema())["CustomerRestApiTests.cs"];

        Assert.Contains("OwnerScoping_AnotherCallerInTheSameTenantIsDeniedTheRow", suite);
        Assert.Contains("FoundryTestEnvironment.AsOtherUser()", suite);
        Assert.Contains("NotContain(created", suite);
    }

    [Fact]
    public void TheDenialAssertionShipsWithItsPositiveControl()
    {
        // A denial assertion is vacuously true when creation or listing is broken: "the other
        // caller cannot see it" holds when nobody can see anything. A real-time probe in this
        // project claimed exactly that and had to be retracted. The control is not optional
        // decoration, so it is asserted separately rather than assumed to travel with the denial.
        var suite = Generate(OwnerScopedSchema())["CustomerRestApiTests.cs"];

        Assert.Contains("OwnerScoping_TheOwnerCanReadBackTheirOwnRow", suite);
        Assert.Contains("Contain(created", suite);
    }

    [Fact]
    public void AnEntityThatDoesNotDeclareOwnerScopingGetsNoOwnershipAssertions()
    {
        // Emitting them anyway would assert a property the schema never claimed, and fail against
        // a correct application -- the same defect as the REST and GraphQL suites that used to be
        // written for every entity regardless of what it declared.
        var suite = Generate(Schema())["CustomerRestApiTests.cs"];

        Assert.DoesNotContain("OwnerScoping_", suite);
        Assert.DoesNotContain("AsOtherUser", suite);
    }

    [Fact]
    public void TheExemptRoleAssertionAppearsOnlyWhenRolesAreDeclared()
    {
        // An exemption that does not exempt is as wrong as a filter that does not filter, and this
        // is the only assertion that can tell correct scoping from a repository returning nothing
        // to anybody. It must not be emitted when the schema declares no exempt roles, because
        // then there is no role to hold.
        var without = Generate(OwnerScopedSchema())["CustomerRestApiTests.cs"];
        Assert.DoesNotContain("OwnerScoping_AnExemptRoleStillSeesTheRow", without);

        var with = Generate(OwnerScopedSchema(exemptRoles: ["Admin"]))["CustomerRestApiTests.cs"];
        Assert.Contains("OwnerScoping_AnExemptRoleStillSeesTheRow", with);
        Assert.Contains("ownerExemptRoles [Admin]", with);
    }

    [Fact]
    public void OwnershipAssertionsAreSkippedWhenThereIsNoWriteMethodToCreateARowWith()
    {
        // Every ownership assertion begins by creating a row to be denied. With no POST there is
        // nothing to create, and emitting them would produce a suite that fails against a correct
        // read-only entity.
        var suite = Generate(OwnerScopedSchema(methods: ["GET"]))["CustomerRestApiTests.cs"];

        Assert.DoesNotContain("OwnerScoping_", suite);
    }

    [Fact]
    public void OwnerScopedWithNoOwnerKeySaysSoInTheFileRatherThanEmittingNothing()
    {
        // FDY3013 should reject this combination at compile time. If it ever does not, the suite
        // must not silently drop the assertion and read as though ownership were covered.
        var suite = Generate(OwnerScopedSchema(withOwnerKey: false))["CustomerRestApiTests.cs"];

        Assert.DoesNotContain("OwnerScoping_AnotherCallerInTheSameTenantIsDeniedTheRow", suite);
        Assert.Contains("no property is marked isOwnerKey", suite);
    }

    [Fact]
    public void TheDenialUsesTheSameTenantSoTenancyCannotExplainThePass()
    {
        // If the second caller were in another tenant, tenant isolation would produce a passing
        // result with owner scoping switched off entirely, and the assertion would prove nothing
        // about ownership. AsOtherUser stays on the primary tenant for exactly this reason.
        var suite = Generate(OwnerScopedSchema())["CustomerRestApiTests.cs"];

        Assert.Contains("AsOtherUser", suite);
        Assert.DoesNotContain("AsOtherTenant", suite);
    }

    [Fact]
    public void TheEnvironmentRefusesToRunUnconfiguredRatherThanPassingQuietly()
    {
        // A suite that skips when its second token is missing reports coverage it does not have.
        var env = Generate(OwnerScopedSchema())["FoundryTestEnvironment.cs"];

        Assert.Contains("FOUNDRY_TEST_TOKEN_OTHER", env);
        Assert.Contains("FOUNDRY_TEST_TOKEN_EXEMPT", env);
        Assert.Contains("throw new InvalidOperationException", env);
    }
}
