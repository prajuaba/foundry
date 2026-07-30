using System.Text.RegularExpressions;
using Foundry.Schema.Compiler;
using Foundry.Schema.Compiler.Generators;
using Xunit;

namespace Foundry.Schema.Compiler.Tests;

/// <summary>
/// The generated client SDKs.
/// </summary>
/// <remarks>
/// <para>
/// Three generators — TypeScript, C# and Python — and none had ever been executed by a test. Every
/// SDK they produced called <c>/api/v1/{singular}</c> while the application serves
/// <c>/api/{plural}</c>, so every request 404'd; the C# one did not compile; and the TypeScript one
/// declared lower-cased field names against a wire format that carries them as declared, so every
/// field read back <c>undefined</c>.
/// </para>
/// <para>
/// The route mistake is the same one found and fixed in Studio's designer and playground. It was
/// fixed there and left here, which is the argument for deriving a route from
/// <see cref="ApiManifestGenerator"/> rather than composing one — a rule fixed in one copy is not
/// fixed.
/// </para>
/// </remarks>
public class SdkGeneratorTests
{
    private static SchemaModel Schema() => new()
    {
        Namespace = "Test.Domain",
        Enums = [new Enum { Name = "CustomerTier", Values = ["Standard", "Premium"] }],
        Entities =
        [
            new Entity
            {
                Name = "Customer",
                ApiEnabledMethods = ["GET", "POST", "GET_BY_ID", "DELETE"],
                Properties =
                [
                    new Property { Name = "Id", Type = "ObjectId", IsKey = true },
                    new Property { Name = "FullName", Type = "string" },
                    new Property { Name = "CreditLimit", Type = "decimal" },
                    new Property { Name = "Tier", Type = "CustomerTier", IsEnum = true }
                ]
            },
            // Pluralises irregularly, so a generator that lower-cases the singular is caught.
            new Entity
            {
                Name = "Category",
                ApiEnabledMethods = ["GET"],
                Properties = [new Property { Name = "Id", Type = "ObjectId", IsKey = true }]
            }
        ]
    };

    private static IEnumerable<string> RoutesIn(string code) =>
        Regex.Matches(code, @"/api/[A-Za-z0-9/_-]*").Select(m => m.Value).Distinct();

    public static TheoryData<string, Func<SchemaModel, string>> AllGenerators => new()
    {
        { "typescript", TypeScriptSdkGenerator.Generate },
        { "csharp", CsharpSdkGenerator.Generate },
        { "python", PythonSdkGenerator.Generate },
    };

    // ── The route contract, in every language ───────────────────────────────

    [Theory]
    [MemberData(nameof(AllGenerators))]
    public void EverySdkCallsTheRouteTheApplicationServes(string language, Func<SchemaModel, string> generate)
    {
        var code = generate(Schema());

        Assert.Contains("/api/customers", code);
        Assert.Contains("/api/categories", code);
        Assert.False(string.IsNullOrEmpty(language));
    }

    [Theory]
    [MemberData(nameof(AllGenerators))]
    public void NoSdkEmitsAVersionSegment(string language, Func<SchemaModel, string> generate)
    {
        // The compiler generates no /api/v1/ prefix and never has.
        Assert.DoesNotContain("/api/v1/", generate(Schema()));
        Assert.False(string.IsNullOrEmpty(language));
    }

    [Theory]
    [MemberData(nameof(AllGenerators))]
    public void NoSdkAddressesAnEntityBySingularName(string language, Func<SchemaModel, string> generate)
    {
        // "/api/customer" would be a 404 against "/api/customers", and is what the singular
        // lower-casing produced.
        var routes = RoutesIn(generate(Schema())).ToList();

        Assert.DoesNotContain("/api/customer", routes);
        Assert.DoesNotContain("/api/category", routes);
        Assert.False(string.IsNullOrEmpty(language));
    }

    [Theory]
    [MemberData(nameof(AllGenerators))]
    public void EveryRouteMatchesTheManifestGenerator(string language, Func<SchemaModel, string> generate)
    {
        // Pinned against the single producer rather than a literal, so the SDKs cannot drift from
        // the manifest the way they already did once.
        var expected = new[] { ApiManifestGenerator.RouteFor("Customer"), ApiManifestGenerator.RouteFor("Category") };
        var code = generate(Schema());

        foreach (var route in expected) Assert.Contains(route, code);
        Assert.False(string.IsNullOrEmpty(language));
    }

    // ── C# ──────────────────────────────────────────────────────────────────

    [Fact]
    public void TheCsharpSdkDoesNotLeakTheDatabaseIdType()
    {
        // `public ObjectId Id` in a file with no `using MongoDB.Bson` and no driver reference: it did
        // not compile, and a REST client has no business taking a MongoDB dependency to read an id
        // the API sends as a JSON string.
        var code = CsharpSdkGenerator.Generate(Schema());

        Assert.DoesNotContain("ObjectId", code);
        Assert.Contains("public string Id { get; set; }", code);
    }

    [Fact]
    public void TheCsharpSdkDeclaresTheEnumsItUses()
    {
        // Referring to a type nothing declares is CS0246 -- and because the SDK ships as source, that
        // is a build error in the consumer's project rather than in ours.
        var code = CsharpSdkGenerator.Generate(Schema());

        Assert.Contains("public enum CustomerTier", code);
        Assert.Contains("Standard", code);
        Assert.Contains("public CustomerTier Tier { get; set; }", code);
    }

    [Fact]
    public void TheCsharpSdkMapsScalarsToClientTypes()
    {
        var code = CsharpSdkGenerator.Generate(Schema());

        Assert.Contains("public decimal CreditLimit { get; set; }", code);
        Assert.Contains("public string FullName { get; set; }", code);
    }

    // ── TypeScript ──────────────────────────────────────────────────────────

    [Fact]
    public void TheTypeScriptSdkNamesFieldsAsTheWireCarriesThem()
    {
        // The API applies no JSON naming policy, so it serialises "FullName". These were lower-cased,
        // which TypeScript compiles happily and which reads back undefined at runtime -- a failure
        // with no error anywhere.
        var code = TypeScriptSdkGenerator.Generate(Schema());

        Assert.Contains("FullName", code);
        Assert.Contains("CreditLimit", code);
        Assert.DoesNotContain("fullname", code);
        Assert.DoesNotContain("creditlimit", code);
    }

    [Fact]
    public void TheTypeScriptSdkMarksTheKeyOptional()
    {
        // A client creating a record does not supply the id.
        Assert.Contains("Id?: string;", TypeScriptSdkGenerator.Generate(Schema()));
    }

    // ── The surface each SDK exposes ────────────────────────────────────────
    //
    // All three generators decided this for themselves and all three decided it the same wrong way:
    // every entity got getAll/getById/create/delete whatever its apiEnabledMethods said, and none of
    // them got update even though PUT is the commonest declared method after GET. So the SDKs offered
    // calls that answer 405 and omitted one the API serves. SdkSurface answers it once now.

    private static SchemaModel SurfaceSchema(params string[] methods) => new()
    {
        Namespace = "Sales.Domain",
        Entities =
        [
            new Entity
            {
                Name = "Order",
                ApiEnabledMethods = [.. methods],
                Properties =
                [
                    new Property { Name = "Id", Type = "ObjectId", IsKey = true },
                    new Property { Name = "TenantId", Type = "string", IsTenantKey = true },
                    new Property { Name = "Reference", Type = "string", Attributes = ["Required"] },
                    new Property { Name = "Note", Type = "string" }
                ]
            }
        ]
    };

    [Fact]
    public void NoSdkOffersDeleteForAnEntityThatDoesNotServeIt()
    {
        var schema = SurfaceSchema("GET", "POST");

        Assert.DoesNotContain("async delete(", TypeScriptSdkGenerator.Generate(schema));
        Assert.DoesNotContain("def delete_order", PythonSdkGenerator.Generate(schema));
        Assert.DoesNotContain("DeleteAsync", CsharpSdkGenerator.Generate(schema));
    }

    [Fact]
    public void EverySdkOffersUpdateWhenTheEntityServesPut()
    {
        // Missing entirely from all three, in every SDK the framework has ever produced.
        var schema = SurfaceSchema("GET", "PUT");

        Assert.Contains("async update(", TypeScriptSdkGenerator.Generate(schema));
        Assert.Contains("def update_order", PythonSdkGenerator.Generate(schema));
        Assert.Contains("UpdateAsync", CsharpSdkGenerator.Generate(schema));
    }

    [Fact]
    public void AnEntityWithNoApiSurfaceGetsNoClient()
    {
        var schema = SurfaceSchema();

        Assert.DoesNotContain("OrderClient", TypeScriptSdkGenerator.Generate(schema));
        Assert.DoesNotContain("def get_all_order", PythonSdkGenerator.Generate(schema));
        Assert.DoesNotContain("class OrderClient", CsharpSdkGenerator.Generate(schema));
    }

    // ── Authentication ──────────────────────────────────────────────────────

    [Fact]
    public void TheTypeScriptClientSendsABearerToken()
    {
        // Every generated endpoint calls RequireAuthorization(), and this client sent no
        // Authorization header at all -- so every call it made answered 401.
        var sdk = TypeScriptSdkGenerator.Generate(SurfaceSchema("GET"));

        Assert.Contains("token?: string;", sdk);
        Assert.Contains("Bearer ${config.token}", sdk);
    }

    [Fact]
    public void ThePythonClientSendsABearerToken()
    {
        var sdk = PythonSdkGenerator.Generate(SurfaceSchema("GET"));

        Assert.Contains("token: Optional[str]", sdk);
        Assert.Contains("f'Bearer {token}'", sdk);
    }

    // ── Failures are not returned as data ───────────────────────────────────

    [Fact]
    public void TheTypeScriptClientRefusesToParseAFailedResponse()
    {
        // It called res.json() without looking at the status, so a 401 body was parsed and handed
        // back typed as the entity the caller asked for. A failure that arrives typed as success is
        // the worst shape a client can have.
        var sdk = TypeScriptSdkGenerator.Generate(SurfaceSchema("GET", "POST"));

        Assert.Contains("class FoundryApiError", sdk);
        Assert.Contains("ensureOk(res, url)", sdk);
    }

    [Fact]
    public void TheCsharpClientChecksTheStatusBeforeDeserialising()
    {
        var sdk = CsharpSdkGenerator.Generate(SurfaceSchema("POST"));

        Assert.Contains("EnsureSuccessStatusCode", sdk);
    }

    // ── What a caller must supply ───────────────────────────────────────────

    [Fact]
    public void ServerAssignedPropertiesAreNotDemandedFromTheCaller()
    {
        // Only the key used to be optional, so a caller had to construct every field to satisfy the
        // type -- including the tenant key, which the server stamps from their token and refuses to
        // take from a request body.
        var sdk = TypeScriptSdkGenerator.Generate(SurfaceSchema("GET", "POST"));

        Assert.Contains("Id?: string;", sdk);
        Assert.Contains("TenantId?: string;", sdk);
        Assert.Contains("Note?: string;", sdk);

        // The control: a property the schema marks Required stays required.
        Assert.Contains("Reference: string;", sdk);
    }
}
