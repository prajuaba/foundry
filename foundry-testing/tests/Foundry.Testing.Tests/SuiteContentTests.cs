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
        string[]? exemptRoles = null, bool withOwnerKey = true, string[]? methods = null,
        string[]? readExemptRoles = null) => new()
    {
        Namespace = "Sales.Domain",
        Entities =
        [
            new Entity
            {
                Name = "Customer",
                OwnerScoped = true,
                OwnerExemptRoles = [.. exemptRoles ?? Array.Empty<string>()],
                OwnerReadExemptRoles = [.. readExemptRoles ?? Array.Empty<string>()],
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

    private static SchemaModel TenantSchema(bool multiTenant = true, string[]? methods = null) => new()
    {
        Namespace = "Sales.Domain",
        Entities =
        [
            new Entity
            {
                Name = "Customer",
                MultiTenant = multiTenant,
                TenantProperty = "TenantId",
                ApiEnabledMethods = [.. methods ?? ["GET", "POST"]],
                Properties =
                [
                    new Property { Name = "Id", Type = "ObjectId", IsKey = true },
                    new Property { Name = "TenantId", Type = "string" },
                    new Property { Name = "Email", Type = "string", Attributes = ["Required"] }
                ]
            }
        ]
    };

    private static SchemaModel MaskedSchema(string attribute = "MaskEmail", bool graphQl = false) => new()
    {
        Namespace = "Sales.Domain",
        Entities =
        [
            new Entity
            {
                Name = "Customer",
                GraphQlEnabled = graphQl,
                ApiEnabledMethods = ["GET", "POST"],
                Properties =
                [
                    new Property { Name = "Id", Type = "ObjectId", IsKey = true },
                    new Property { Name = "Email", Type = "string", Attributes = [attribute] }
                ]
            }
        ]
    };

    private static SchemaModel SensitiveSchema(Property masked, bool graphQl = false) => new()
    {
        Namespace = "Sales.Domain",
        Entities =
        [
            new Entity
            {
                Name = "Customer",
                GraphQlEnabled = graphQl,
                ApiEnabledMethods = ["GET", "POST"],
                Properties = [new Property { Name = "Id", Type = "ObjectId", IsKey = true }, masked]
            }
        ]
    };

    /// <summary>The source of one generated test method, so assertions cannot match its neighbours.</summary>
    private static string Method(string suite, string name)
    {
        var start = suite.IndexOf($"Task {name}()", StringComparison.Ordinal);
        Assert.True(start >= 0, $"{name} was not generated");
        var end = suite.IndexOf("[Fact]", start, StringComparison.Ordinal);
        return end < 0 ? suite[start..] : suite[start..end];
    }

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
        //
        // The payload now lives in the seed registry rather than inline in each suite, because a
        // foreign key can only be filled by a row that was created first.
        var seed = Generate(Schema())["FoundrySeed.cs"];

        Assert.Contains("\"Email\"", seed);
        Assert.DoesNotContain("AutoTest", seed);
    }

    [Fact]
    public void TheSuitesTakeTheirPayloadFromTheSeederRatherThanALiteral()
    {
        // Every write in the suite goes through the seeder. A literal payload sends the default
        // ObjectId for every reference, which the application refuses -- before any access-control
        // assertion has run, so the entity is reported covered and is not.
        var suite = Generate(Schema())["CustomerRestApiTests.cs"];

        Assert.Contains("FoundrySeed.PayloadForAsync", suite);
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

    [Fact]
    public void TheReadExemptRoleIsAssertedSeparatelyFromTheWiderExemption()
    {
        // EntityAccessPolicy exempts ownerExemptRoles on reads and writes, and
        // ownerReadExemptRoles on reads only -- two declarations, not one. Covering the wider
        // list alone would leave the narrower one declared and unverified, which is the defect
        // this suite exists to catch, one declaration over.
        var without = Generate(OwnerScopedSchema())["CustomerRestApiTests.cs"];
        Assert.DoesNotContain("OwnerScoping_AReadExemptRoleSeesTheRow", without);

        var with = Generate(OwnerScopedSchema(readExemptRoles: ["Auditor"]))["CustomerRestApiTests.cs"];
        Assert.Contains("OwnerScoping_AReadExemptRoleSeesTheRow", with);
        Assert.Contains("ownerReadExemptRoles [Auditor]", with);
        Assert.Contains("AsReadExemptRole", with);
    }

    // ── Tenancy ─────────────────────────────────────────────────────────────

    [Fact]
    public void AMultiTenantEntityGetsADenialAndItsControl()
    {
        var suite = Generate(TenantSchema())["CustomerRestApiTests.cs"];

        Assert.Contains("Tenancy_ARowIsNotVisibleFromAnotherTenant", suite);
        Assert.Contains("FoundryTestEnvironment.AsOtherTenant()", suite);

        // The control is asserted separately rather than assumed to travel with the denial: "the
        // other tenant cannot see it" is true whenever nobody can see anything.
        Assert.Contains("Tenancy_TheOwningTenantSeesItsOwnRow", suite);
    }

    [Fact]
    public void AnEntityThatIsNotMultiTenantGetsNoTenancyAssertions()
    {
        var suite = Generate(TenantSchema(multiTenant: false))["CustomerRestApiTests.cs"];

        Assert.DoesNotContain("Tenancy_", suite);
    }

    [Fact]
    public void TenancyAssertionsAreSkippedWithoutAWriteMethod()
    {
        // Every tenancy assertion begins by creating a row to be denied. With no POST there is
        // nothing to create, and emitting them would fail against a correct read-only entity.
        var suite = Generate(TenantSchema(methods: ["GET"]))["CustomerRestApiTests.cs"];

        Assert.DoesNotContain("Tenancy_", suite);
    }

    // ── Masking ─────────────────────────────────────────────────────────────

    [Fact]
    public void AMaskedPropertyIsProbedWithAValueThatWasActuallySet()
    {
        // The showcase once asserted that no card number appeared in a payload it had never put
        // one in -- a green check for a redaction that never ran. The sentinel has to be written
        // before its absence means anything.
        var suite = Generate(MaskedSchema())["CustomerRestApiTests.cs"];

        Assert.Contains("Protection_Email_IsNotReturnedInTheClear", suite);
        Assert.Contains("CreateRowWithAsync", suite);
        Assert.Contains("unmasked-sentinel@example.com", suite);
    }

    [Fact]
    public void TheMaskingAssertionRequiresTheRowToComeBack()
    {
        // Absence of the sentinel is satisfied by an empty response, a dropped field and a write
        // that never happened. The row's own id must be in the body too.
        var suite = Generate(MaskedSchema())["CustomerRestApiTests.cs"];

        Assert.Contains("Should().Contain(created", suite);
    }

    [Fact]
    public void AnEncryptedPropertyGetsNoReadAssertion()
    {
        // Repository.ProtectForRead decrypts and then masks, so an Encrypt-only property is
        // correctly returned in the clear to a caller allowed to read the row. Asserting its
        // absence would fail against a working system: encryption protects the stored document,
        // not the response.
        var suite = Generate(MaskedSchema(attribute: "Encrypt"))["CustomerRestApiTests.cs"];

        Assert.DoesNotContain("Protection_Email", suite);
    }

    [Fact]
    public void AMaskedPropertyIsAlsoProbedThroughTheResolver()
    {
        // Findings 7 and 8: protected over REST and raw over GraphQL, because the resolver had no
        // materialised entity to mask.
        var suite = Generate(MaskedSchema(graphQl: true))["CustomerGraphQLTests.cs"];

        Assert.Contains("Protection_Email_IsNotReturnedInTheClearThroughTheResolver", suite);

        // Queried by the name GraphQL exposes. Asking for the PascalCase name returns an unknown
        // field error, and the sentinel really is absent from an error response.
        Assert.Contains("email", suite);
    }

    [Fact]
    public void AMaskedPropertyWithSensitiveRolesIsReadAsACallerOutsideThem()
    {
        // Resourcify put Admin in sensitiveRoles and every masking assertion read as the primary
        // identity -- an Admin -- which was correctly shown the value. The row is still written by
        // the primary identity; only the read moves.
        var masked = new Property
        {
            Name = "Phone", Type = "string", Attributes = ["Mask"], SensitiveRoles = ["Admin"]
        };

        foreach (var (file, test) in new[]
        {
            ("CustomerRestApiTests.cs", "Protection_Phone_IsNotReturnedInTheClear"),
            ("CustomerGraphQLTests.cs", "Protection_Phone_IsNotReturnedInTheClearThroughTheResolver")
        })
        {
            var method = Method(Generate(SensitiveSchema(masked, graphQl: true))[file], test);

            Assert.Contains("using var writer = FoundryTestEnvironment.Authenticated();", method);
            Assert.Contains("using var reader = FoundryTestEnvironment.AsUnentitledToSensitiveData();", method);
            Assert.Matches(@"RowWithAsync\(writer,", method);
            Assert.Matches(@"(ReadRowAsync|QueryFieldAsync)\(reader,", method);
        }
    }

    [Fact]
    public void AMaskedPropertyWithoutSensitiveRolesNeedsNoExtraIdentity()
    {
        // Only a scope claim unmasks it, which the primary identity is not expected to carry.
        // Requiring a fourth token here would break every suite that never declared a role.
        var method = Method(Generate(MaskedSchema())["CustomerRestApiTests.cs"],
            "Protection_Email_IsNotReturnedInTheClear");

        Assert.Contains("using var reader = FoundryTestEnvironment.Authenticated();", method);
        Assert.DoesNotContain("AsUnentitledToSensitiveData", method);
    }

    [Fact]
    public void TheEnvironmentNamesTheUnentitledIdentitysVariable()
    {
        var environment = Generate(MaskedSchema())["FoundryTestEnvironment.cs"];

        Assert.Contains("public static HttpClient AsUnentitledToSensitiveData()", environment);
        Assert.Contains("FOUNDRY_TEST_TOKEN_UNENTITLED", environment);
    }

    [Theory]
    [InlineData("decimal", "Range(0, 100000)", "73519.27m")]
    [InlineData("decimal", null, "73519.27m")]
    [InlineData("int", "Range(0, 100000)", "73519")]
    [InlineData("decimal", "Range(0, 100)", "73.52m")]
    [InlineData("int", "Range(10, 20)", "17")]
    public void AMaskedNumberIsProbedWithANumberItsRangeAccepts(string type, string? range, string literal)
    {
        // A string sentinel posted into a decimal was refused by the binder with 400, so ten of
        // Resourcify's masking assertions failed on the write and never reached a read.
        var masked = new Property
        {
            Name = "Rate", Type = type,
            Attributes = range is null ? ["Mask"] : ["Mask", range]
        };

        var method = Method(Generate(SensitiveSchema(masked))["CustomerRestApiTests.cs"],
            "Protection_Rate_IsNotReturnedInTheClear");

        Assert.Contains($@"CreateRowWithAsync(writer, ""Rate"", {literal});", method);
        Assert.DoesNotContain("SENTINEL", method);
        Assert.Contains($@"NotContain(""{literal.TrimEnd('m')}""", method);
    }

    [Theory]
    [InlineData("Phone", "string", "Mask", "+15550137019")]
    [InlineData("Contact", "string", "MaskEmail", "unmasked-sentinel@example.com")]
    [InlineData("Code", "string", "Mask", "SENTINEL-0123456789")]
    [InlineData("Rate", "decimal", "Mask", 73519.27)]
    public void TheNeedleIsFoundInAnUnmaskedBodyAsTheApplicationWritesIt(
        string name, string type, string attribute, object value)
    {
        // Resourcify's REST phone assertion passed against an unmasked number: System.Text.Json
        // writes '+' as \u002B, so the literal the test searched for was never in the body. This
        // serialises a genuinely unmasked row the way ASP.NET does and requires the generated
        // needle to find it -- the assertion must be able to fail.
        var masked = new Property { Name = name, Type = type, Attributes = [attribute] };
        var method = Method(Generate(SensitiveSchema(masked))["CustomerRestApiTests.cs"],
            $"Protection_{name}_IsNotReturnedInTheClear");

        var needle = System.Text.RegularExpressions.Regex
            .Match(method, @"NotContain\(""([^""]+)""").Groups[1].Value;
        Assert.NotEmpty(needle);

        var unmasked = System.Text.Json.JsonSerializer.Serialize(
            new Dictionary<string, object> { [name] = type == "decimal" ? Convert.ToDecimal(value) : value },
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        Assert.Contains(needle, unmasked);
    }

    [Fact]
    public void OwnerScopingIsAlsoAssertedThroughTheResolver()
    {
        var schema = OwnerScopedSchema();
        schema = schema with { Entities = [schema.Entities[0] with { GraphQlEnabled = true }] };

        var suite = Generate(schema)["CustomerGraphQLTests.cs"];

        Assert.Contains("OwnerScoping_ANonOwnerIsDeniedThroughTheResolver", suite);
        Assert.Contains("OwnerScoping_TheOwnerDoesSeeTheirRowThroughTheResolver", suite);
    }
}
