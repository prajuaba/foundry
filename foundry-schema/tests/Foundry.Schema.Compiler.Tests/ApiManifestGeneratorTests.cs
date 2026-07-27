using System.Text.Json;
using Foundry.Schema.Compiler;
using Xunit;

namespace Foundry.Schema.Compiler.Tests;

/// <summary>
/// The api-manifest.json contract.
/// </summary>
/// <remarks>
/// <para>
/// This is now the <em>only</em> producer of the manifest. Studio used to derive it independently by
/// walking its canvas, and the two disagreed: Studio emitted <c>/api/v1/{plural}</c> where this emits
/// <c>/api/{plural}</c>, and Studio gave an entity with no declared methods a full CRUD surface where
/// this skips it. An application therefore served different URLs depending on which tool wrote its
/// manifest, and nothing reported a conflict because each manifest was individually valid.
/// </para>
/// <para>
/// These assertions were ported from the Studio test suite when that producer was removed, so the
/// contract they encode is preserved rather than deleted along with the code that used to satisfy it.
/// </para>
/// <para>
/// They are now its <em>only</em> home. Studio kept a mirrored <c>crudRouteFor</c> for display, held
/// to this table by a duplicate of it in TypeScript; the designer and playground read routes from a
/// derived manifest instead, and that duplicate is gone. A shared table catches a derivation that
/// changes and cannot catch a rule the compiler gains, since a rule nobody wrote down twice is not
/// in the table.
/// </para>
/// </remarks>
public class ApiManifestGeneratorTests
{
    private static SchemaModel Schema(params Entity[] entities) =>
        new() { Namespace = "Test.Domain", Entities = [.. entities] };

    private static Entity EntityWith(string name, params string[] methods) => new()
    {
        Name = name,
        Properties = [new Property { Name = "Id", Type = "ObjectId", IsKey = true }],
        ApiEnabledMethods = [.. methods]
    };

    private static JsonElement Generate(SchemaModel schema) =>
        JsonDocument.Parse(ApiManifestGenerator.Generate(schema)).RootElement.Clone();

    private static JsonElement[] Endpoints(SchemaModel schema) =>
        [.. Generate(schema).GetProperty("Endpoints").EnumerateArray()];

    // ---- routes ----

    [Theory]
    [InlineData("Customer", "/api/customers")]
    [InlineData("Category", "/api/categories")]
    [InlineData("Address", "/api/addresses")]
    [InlineData("Box", "/api/boxes")]
    [InlineData("Branch", "/api/branches")]
    [InlineData("Day", "/api/days")]
    [InlineData("Order", "/api/orders")]
    public void RoutesArePluralisedAndUnversioned(string entityName, string expectedRoute)
    {
        var endpoint = Assert.Single(Endpoints(Schema(EntityWith(entityName, "GET"))));

        Assert.Equal(expectedRoute, endpoint.GetProperty("Route").GetString());
    }

    [Fact]
    public void RouteDerivationIsDeterministic()
    {
        // The IR carries no per-entity CRUD route, so the same entity name must always yield the same
        // route. If it did not, regenerating a manifest would silently move a published endpoint.
        var first = ApiManifestGenerator.Generate(Schema(EntityWith("Customer", "GET")));
        var second = ApiManifestGenerator.Generate(Schema(EntityWith("Customer", "GET")));

        Assert.Equal(first, second);
    }

    // ---- which entities are exposed ----

    [Fact]
    public void AnEntityWithNoDeclaredMethodsIsSkipped()
    {
        // Not defaulted to full CRUD. An entity that exists only as a workflow target or a DTO source
        // must not acquire a public surface -- DELETE included -- merely by being present.
        Assert.Empty(Endpoints(Schema(EntityWith("AuditRecord"))));
    }

    [Fact]
    public void OnlyDeclaredMethodsAreExposed()
    {
        var endpoint = Assert.Single(Endpoints(Schema(EntityWith("Customer", "GET", "POST"))));
        var methods = endpoint.GetProperty("Methods").EnumerateArray().Select(m => m.GetString()).ToList();

        Assert.Equal(["GET", "POST"], methods);
        Assert.DoesNotContain("DELETE", methods);
    }

    [Fact]
    public void UnknownMethodsAreDropped()
    {
        var endpoint = Assert.Single(Endpoints(Schema(EntityWith("Customer", "GET", "TELEPORT"))));
        var methods = endpoint.GetProperty("Methods").EnumerateArray().Select(m => m.GetString()).ToList();

        Assert.Equal(["GET"], methods);
    }

    [Fact]
    public void MethodNamesAreNormalisedToUppercase()
    {
        var endpoint = Assert.Single(Endpoints(Schema(EntityWith("Customer", "get", "post"))));
        var methods = endpoint.GetProperty("Methods").EnumerateArray().Select(m => m.GetString()).ToList();

        Assert.Equal(["GET", "POST"], methods);
    }

    [Fact]
    public void OneEndpointGroupPerEntity()
    {
        var entities = Endpoints(Schema(EntityWith("Customer", "GET"), EntityWith("Order", "GET")))
            .Select(e => e.GetProperty("Entity").GetString())
            .OrderBy(n => n)
            .ToList();

        Assert.Equal(["Customer", "Order"], entities);
    }

    // ---- roles and caching ----

    [Fact]
    public void PerMethodRolesArePropagated()
    {
        var entity = EntityWith("Invoice", "GET", "DELETE") with
        {
            ApiRoles = new() { ["DELETE"] = ["Admin"] }
        };

        var endpoint = Assert.Single(Endpoints(Schema(entity)));

        Assert.Equal(
            ["Admin"],
            endpoint.GetProperty("Roles").GetProperty("DELETE").EnumerateArray().Select(r => r.GetString()));
    }

    [Fact]
    public void RolesForUnexposedMethodsAreDropped()
    {
        // A role entry for a method nobody can call is misleading when auditing access.
        var entity = EntityWith("Invoice", "GET") with
        {
            ApiRoles = new() { ["DELETE"] = ["Admin"] }
        };

        var endpoint = Assert.Single(Endpoints(Schema(entity)));

        Assert.False(endpoint.GetProperty("Roles").TryGetProperty("DELETE", out _));
    }

    [Fact]
    public void CachingIsPropagatedForExposedMethods()
    {
        var entity = EntityWith("Customer", "GET") with
        {
            ApiCaching = new() { ["GET"] = new ApiCachingConfig { Enabled = true, TtlSeconds = 30 } }
        };

        var caching = Assert.Single(Endpoints(Schema(entity))).GetProperty("Caching").GetProperty("GET");

        Assert.True(caching.GetProperty("Enabled").GetBoolean());
        Assert.Equal(30, caching.GetProperty("TtlSeconds").GetInt32());
    }

    // ---- manifest shape ----

    [Fact]
    public void TheNamespaceIsCarried()
    {
        Assert.Equal("Test.Domain", Generate(Schema(EntityWith("Customer", "GET"))).GetProperty("Namespace").GetString());
    }

    [Fact]
    public void CollectionsTheRuntimeBindsArePresentEvenWhenEmpty()
    {
        // ApiManifest binds Endpoints and CustomEndpoints as non-null lists. Omitting one leaves the
        // runtime with an empty list, which serves no routes rather than reporting a malformed manifest.
        var manifest = Generate(new SchemaModel { Namespace = "Test.Domain" });

        Assert.Equal(JsonValueKind.Array, manifest.GetProperty("Endpoints").ValueKind);
        Assert.Equal(JsonValueKind.Array, manifest.GetProperty("CustomEndpoints").ValueKind);
    }

    [Fact]
    public void CustomEndpointsArePropagated()
    {
        var schema = new SchemaModel
        {
            Namespace = "Test.Domain",
            Entities = [EntityWith("Order", "GET")],
            CustomEndpoints =
            [
                new CustomEndpoint { Route = "/api/orders/submit", Method = "post", RequestType = "SubmitOrderCommand" }
            ]
        };

        var custom = Assert.Single(Generate(schema).GetProperty("CustomEndpoints").EnumerateArray());

        Assert.Equal("/api/orders/submit", custom.GetProperty("Route").GetString());
        Assert.Equal("POST", custom.GetProperty("Method").GetString());
        Assert.Equal("SubmitOrderCommand", custom.GetProperty("RequestType").GetString());
    }

    [Fact]
    public void ACustomEndpointWithNoRouteIsSkipped()
    {
        var schema = new SchemaModel
        {
            Namespace = "Test.Domain",
            CustomEndpoints = [new CustomEndpoint { Route = "", Method = "GET" }]
        };

        Assert.Empty(Generate(schema).GetProperty("CustomEndpoints").EnumerateArray());
    }

    [Fact]
    public void ANullSchemaIsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => ApiManifestGenerator.Generate(null!));
    }

    [Fact]
    public void TheOutputIsIndentedForHumanReview()
    {
        // The manifest is committed alongside a project and read in diffs.
        Assert.Contains("\n", ApiManifestGenerator.Generate(Schema(EntityWith("Customer", "GET"))));
    }
}
