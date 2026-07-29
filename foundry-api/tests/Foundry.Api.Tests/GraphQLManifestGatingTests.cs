using System.Linq;
using System.Threading.Tasks;
using HotChocolate;
using HotChocolate.Execution;
using Microsoft.Extensions.DependencyInjection;
using Foundry.Api.Manifest;
using Paperclip.OrderingSystem.Domain;
using Xunit;

namespace Foundry.Api.Tests;

/// <summary>
/// The GraphQL schema offers what the manifest declares, and nothing more.
/// </summary>
/// <remarks>
/// Every entity used to get all five operations regardless of what it declared, so an entity
/// published read-only over REST still accepted <c>create</c>, <c>update</c> and <c>delete</c> over
/// GraphQL. The manifest is the single statement of what an API offers; two transports reading it
/// differently is the same defect class as two implementations of a route.
/// </remarks>
public class GraphQLManifestGatingTests
{
    private static ApiManifest ManifestFor(bool graphQl, params string[] methods) => new()
    {
        Namespace = typeof(Order).Namespace!,
        Endpoints =
        [
            new EndpointConfig
            {
                Route = "/api/orders",
                Entity = nameof(Order),
                Methods = [.. methods],
                Roles = { ["GET"] = ["Admin"] },
                GraphQL = graphQl
            }
        ]
    };

    private static async Task<ISchema> SchemaForAsync(params string[] methods)
    {
        var manifest = ManifestFor(graphQl: true, methods);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDynamicGraphQL(manifest);

        var resolver = services.BuildServiceProvider().GetRequiredService<IRequestExecutorResolver>();
        var executor = await resolver.GetRequestExecutorAsync();

        return executor.Schema;
    }

    [Fact]
    public async Task AReadOnlyEntityHasNoMutations()
    {
        var schema = await SchemaForAsync("GET", "GET_BY_ID");

        // No Mutation type at all, rather than an empty one. HotChocolate rejects a root type with no
        // fields, so gating the *fields* without gating the *type* would have turned a read-only
        // manifest into a schema that throws on first request — reintroducing the original defect in a
        // narrower case.
        Assert.Null(schema.MutationType);
        Assert.Contains("getOrders", schema.QueryType.Fields.Select(f => f.Name));
    }

    [Fact]
    public async Task AManifestWithNothingReadableIsRefusedAtStartup()
    {
        // The Query type has the same "at least one field" rule, but nothing can be gated away to
        // rescue it. Refusing at registration puts the failure where an operator will see it, instead
        // of in the response to whoever happens to send the first query.
        var error = await Assert.ThrowsAsync<System.InvalidOperationException>(
            () => SchemaForAsync("POST", "DELETE"));

        Assert.Contains("no readable entity", error.Message);
    }

    [Fact]
    public async Task AnEntityGetsExactlyTheOperationsItDeclares()
    {
        var schema = await SchemaForAsync("GET", "POST");

        Assert.Contains("getOrders", schema.QueryType.Fields.Select(f => f.Name));
        Assert.DoesNotContain("getOrderById", schema.QueryType.Fields.Select(f => f.Name));
        Assert.Contains("createOrder", schema.MutationType!.Fields.Select(f => f.Name));
        Assert.DoesNotContain("deleteOrder", schema.MutationType.Fields.Select(f => f.Name));
    }

    // ── Opting in ───────────────────────────────────────────────────────────

    [Fact]
    public async Task AnEntityThatDidNotOptInIsNotExposedOverGraphQL()
    {
        // `enableGraphQL` is per entity in the schema and did not travel as far as the manifest, so
        // the code that builds the GraphQL surface could not read it: every entity declaring a GET
        // was served over GraphQL whether or not it asked to be, and the field decided nothing.
        var services = new ServiceCollection();
        services.AddLogging();

        // At registration rather than on the first query: an application that maps GraphQL and
        // exposes nothing over it is misconfigured, and should fail where an operator is watching.
        var error = Assert.Throws<System.InvalidOperationException>(
            () => services.AddDynamicGraphQL(ManifestFor(graphQl: false, "GET", "POST")));

        Assert.Contains("no readable entity", error.Message);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task OptingInIsWhatPutsAnEntityInTheSchema()
    {
        // The positive half, so the test above cannot pass because the schema is broken for some
        // unrelated reason: the same manifest with the flag set produces the field.
        var schema = await SchemaForAsync("GET", "POST");

        Assert.Contains("getOrders", schema.QueryType.Fields.Select(f => f.Name));
    }
}
