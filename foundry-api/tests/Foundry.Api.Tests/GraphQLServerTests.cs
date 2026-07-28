using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using NSubstitute;
using Foundry.Mongo.Repositories;
using Paperclip.OrderingSystem.Domain;
using Xunit;

namespace Foundry.Api.Tests;

/// <summary>
/// The GraphQL server, which both the template and the sample map.
/// </summary>
/// <remarks>
/// <para>
/// GraphQL had never been run as a server. It was covered as an outbound <em>connector</em> — a
/// different component with the same word in its name — and the resemblance was enough for it to read
/// as covered.
/// </para>
/// <para>
/// It did not work at all. <c>AddDynamicGraphQL</c> built the collection field as
/// <c>ListType&lt;Order&gt;</c>, but <c>ListType&lt;T&gt;</c> constrains <c>T</c> to a GraphQL type,
/// not a CLR entity; the mutations passed the entity's output type where an input type belongs. The
/// schema therefore threw <c>SchemaException</c> during type discovery, on the first request and
/// every request after it, and the exception handler turned that into a bare 500. So
/// <strong>every GraphQL query any Foundry application ever served returned "an error occurred while
/// processing your request"</strong>, with the real cause never reaching a log a caller could see.
/// </para>
/// </remarks>
public class GraphQLServerTests : IClassFixture<AuthenticatedApiFactory>
{
    private readonly AuthenticatedApiFactory _factory;

    static GraphQLServerTests()
    {
        Environment.SetEnvironmentVariable("MONGODB_ENCRYPTION_KEY", "12345678901234567890123456789012");
    }

    public GraphQLServerTests(AuthenticatedApiFactory factory) => _factory = factory;

    private static async Task<(HttpStatusCode Status, JsonElement Body)> QueryAsync(HttpClient client, string query)
    {
        var response = await client.PostAsJsonAsync("/graphql", new { query });
        var text = await response.Content.ReadAsStringAsync();

        // A non-JSON body is itself the finding worth reporting: it is what a schema that cannot be
        // built looks like from the outside.
        JsonElement body;
        try
        {
            body = JsonDocument.Parse(text).RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new Xunit.Sdk.XunitException($"{response.StatusCode} returned a non-JSON body: {text}");
        }

        return (response.StatusCode, body);
    }

    private static IReadOnlyList<string> ErrorCodes(JsonElement body)
    {
        if (!body.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return errors.EnumerateArray()
            .Select(e => e.TryGetProperty("extensions", out var ext) && ext.TryGetProperty("code", out var code)
                ? code.GetString() ?? string.Empty
                : string.Empty)
            .ToList();
    }

    private static string Describe(JsonElement body) => body.ToString();

    private const string CreateOrderMutation = """
        mutation {
          createOrder(input: {
            orderNumber: "ORD-1", customerId: "c-1", totalAmount: 1,
            status: PENDING, secretToken: "t", userEmail: "a@b.c"
          }) { orderNumber }
        }
        """;

    // ── The schema builds ───────────────────────────────────────────────────

    [Fact]
    public async Task TheSchemaBuilds()
    {
        // The whole feature rested on this and it was false. Introspection is the cheapest query
        // there is: if it 500s, nothing else about the endpoint can work either.
        var client = _factory.CreateClient().As("Admin");

        var (status, body) = await QueryAsync(client, "{ __schema { queryType { name } } }");

        Assert.True(status == HttpStatusCode.OK, Describe(body));
        Assert.Empty(ErrorCodes(body));
        Assert.Equal("Query", body.GetProperty("data").GetProperty("__schema")
            .GetProperty("queryType").GetProperty("name").GetString());
    }

    [Fact]
    public async Task TheSchemaExposesTheEntitiesTheManifestDeclares()
    {
        var client = _factory.CreateClient().As("Admin");

        var (_, body) = await QueryAsync(client, "{ __schema { queryType { fields { name } } } }");

        var fields = body.GetProperty("data").GetProperty("__schema").GetProperty("queryType")
            .GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("name").GetString()).ToList();

        Assert.Contains("getOrders", fields);
        Assert.Contains("getOrderById", fields);
    }

    [Fact]
    public async Task MutationsTakeAnInputTypeRatherThanTheEntityType()
    {
        // The specific defect: `NonNullType<Order>` named the *output* type as an argument type.
        // Asserting the argument's type kind pins the fix to the reason for it — a schema that merely
        // builds could still have been built the wrong way.
        var client = _factory.CreateClient().As("Admin");

        var (_, body) = await QueryAsync(client, """
            { __schema { mutationType { fields { name args { name type { kind ofType { kind } } } } } } }
            """);

        var create = body.GetProperty("data").GetProperty("__schema").GetProperty("mutationType")
            .GetProperty("fields").EnumerateArray().Single(f => f.GetProperty("name").GetString() == "createOrder");

        var input = create.GetProperty("args").EnumerateArray()
            .Single(a => a.GetProperty("name").GetString() == "input");

        Assert.Equal("NON_NULL", input.GetProperty("type").GetProperty("kind").GetString());
        Assert.Equal("INPUT_OBJECT", input.GetProperty("type").GetProperty("ofType").GetProperty("kind").GetString());
    }

    // ── The wire contract ───────────────────────────────────────────────────

    [Fact]
    public async Task TheMutationInputHidesWhatTheEntityHidesFromItsWireContract()
    {
        // Order marks IsDeleted and DeletedAt [JsonIgnore], and the comment on the property says why:
        // hiding it "stops a PUT from setting it, which would delete a record via the update route and
        // skip whatever roles the manifest applies to DELETE". HotChocolate builds from CLR properties
        // and never reads System.Text.Json attributes, so `updateOrder(input: { isDeleted: true })`
        // was that same bypass, reopened on the other transport.
        var client = _factory.CreateClient().As("Admin");

        var (_, body) = await QueryAsync(client, """{ __type(name: "OrderInput") { inputFields { name } } }""");

        var fields = InputFieldNames(body);
        Assert.DoesNotContain("isDeleted", fields);
        Assert.DoesNotContain("deletedAt", fields);
    }

    [Fact]
    public async Task TheOutputTypeHidesWhatTheEntityHidesFromItsWireContract()
    {
        var client = _factory.CreateClient().As("Admin");

        var (_, body) = await QueryAsync(client, """{ __type(name: "Order") { fields { name } } }""");

        var fields = body.GetProperty("data").GetProperty("__type").GetProperty("fields")
            .EnumerateArray().Select(f => f.GetProperty("name").GetString()).ToList();

        Assert.DoesNotContain("isDeleted", fields);
        Assert.Contains("orderNumber", fields);
    }

    [Fact]
    public async Task TheMutationInputDoesNotAskTheCallerForServerAssignedState()
    {
        // A non-nullable CLR property becomes a required GraphQL input field, so createOrder demanded
        // id, createdAtUtc, updatedAtUtc and version — all of which the repository assigns and then
        // overwrites. The mutation could not be called correctly by anyone.
        var client = _factory.CreateClient().As("Admin");

        var (_, body) = await QueryAsync(client, """{ __type(name: "OrderInput") { inputFields { name } } }""");

        var fields = InputFieldNames(body);
        Assert.DoesNotContain("id", fields);
        Assert.DoesNotContain("version", fields);
        Assert.DoesNotContain("createdAtUtc", fields);
        Assert.Contains("orderNumber", fields);
    }

    private static IReadOnlyList<string?> InputFieldNames(JsonElement body)
        => body.GetProperty("data").GetProperty("__type").GetProperty("inputFields")
            .EnumerateArray().Select(f => f.GetProperty("name").GetString()).ToList();

    // ── Access ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnonymousCallersAreRefused()
    {
        // MapGraphQL carried no authorization at all, while every REST endpoint beside it did. Once
        // the schema builds, that is a full CRUD surface — including deleteOrder — open to anyone who
        // can reach the port.
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/graphql", new { query = "{ getOrders { id } }" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AFieldIsRefusedToACallerWithoutTheDeclaredRole()
    {
        // The manifest declares POST on Order as Admin-only, and the REST endpoint enforces it.
        // SecurityBehavior cannot: it reads the EndpointConfig from the matched endpoint's metadata,
        // and the GraphQL endpoint carries none, so it returned early for every GraphQL request.
        var client = _factory.CreateClient().As("User");

        var (_, body) = await QueryAsync(client, CreateOrderMutation);

        Assert.True(ErrorCodes(body).Contains("AUTH_NOT_AUTHORIZED"), Describe(body));
    }

    [Fact]
    public async Task AFieldIsAllowedToACallerWithTheDeclaredRole()
    {
        var repo = Substitute.For<IRepository<Order>>();
        repo.Query().Returns(new List<Order>
        {
            new() { Id = ObjectId.GenerateNewId(), OrderNumber = "ORD-001", CustomerId = "c-1", TotalAmount = 9.99m }
        }.AsQueryable());

        var client = _factory
            .WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<IRepository<Order>>(repo)))
            .CreateClient().As("Admin");

        var (status, body) = await QueryAsync(client, "{ getOrders { orderNumber } }");

        Assert.True(status == HttpStatusCode.OK, Describe(body));
        Assert.Empty(ErrorCodes(body));
        Assert.Equal("ORD-001", body.GetProperty("data").GetProperty("getOrders")[0]
            .GetProperty("orderNumber").GetString());
    }

    // ── Reads go through the repository's filters ───────────────────────────

    [Fact]
    public async Task TheCollectionResolverReadsThroughTheFilteredQuery()
    {
        // It read repo.Collection.AsQueryable() — the raw MongoDB collection, which applies no
        // soft-delete filter, no tenant filter and no owner scope. Every tenant's rows and every
        // deleted row were returned to whoever asked, through a door beside the REST path where that
        // exact isolation failure had already been found and fixed.
        var repo = Substitute.For<IRepository<Order>>();
        repo.Query().Returns(new List<Order>().AsQueryable());

        var client = _factory
            .WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<IRepository<Order>>(repo)))
            .CreateClient().As("Admin");

        await QueryAsync(client, "{ getOrders { id } }");

        repo.Received().Query();
        _ = repo.DidNotReceive().Collection;
    }
}
