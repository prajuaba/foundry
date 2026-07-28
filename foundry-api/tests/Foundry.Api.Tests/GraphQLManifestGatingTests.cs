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
    private static async Task<ISchema> SchemaForAsync(params string[] methods)
    {
        var manifest = new ApiManifest
        {
            Namespace = typeof(Order).Namespace!,
            Endpoints =
            [
                new EndpointConfig
                {
                    Route = "/api/orders",
                    Entity = nameof(Order),
                    Methods = [.. methods],
                    Roles = { ["GET"] = ["Admin"] }
                }
            ]
        };

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
}
