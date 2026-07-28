using System.Text.Json;
using Foundry.Schema.Compiler;
using Foundry.Schema.Compiler.Exporters;
using Xunit;

namespace Foundry.Schema.Compiler.Tests;

/// <summary>
/// The four <c>foundry export</c> formats.
/// </summary>
/// <remarks>
/// <para>
/// None of them had a test, and all four exit 0 whatever they emit, so "it works" had only ever
/// meant "the process did not crash".
/// </para>
/// <para>
/// OpenAPI and Postman both composed <c>/api/v1/{lowercase-singular}</c> while the application
/// serves <c>/api/{plural}</c>. That is now the fifth and sixth copy of a route rule found to
/// disagree with the one that runs, after Studio's designer, Studio's playground, and the three SDK
/// generators — which is why these read the route from
/// <see cref="ApiManifestGenerator"/> instead of composing one.
/// </para>
/// </remarks>
public class ExporterTests
{
    private static SchemaModel Schema() => new()
    {
        Namespace = "Test.Domain",
        Version = "2.0.0",
        Entities =
        [
            new Entity
            {
                Name = "Customer",
                ApiEnabledMethods = ["GET", "POST", "GET_BY_ID", "PUT", "DELETE"],
                Properties =
                [
                    new Property { Name = "Id", Type = "ObjectId", IsKey = true },
                    new Property { Name = "FullName", Type = "string" },
                    new Property { Name = "CreditLimit", Type = "decimal" }
                ]
            },
            // Read-only, and pluralises irregularly.
            new Entity
            {
                Name = "Category",
                ApiEnabledMethods = ["GET"],
                Properties = [new Property { Name = "Id", Type = "ObjectId", IsKey = true }]
            },
            // Declares no REST surface at all, so the manifest gives it no endpoints.
            new Entity
            {
                Name = "AuditRecord",
                Properties = [new Property { Name = "Id", Type = "ObjectId", IsKey = true }]
            }
        ],
        CustomEndpoints =
        [
            new CustomEndpoint { Route = "/api/orders/checkout", Method = "POST", RequestType = "PlaceOrderCommand" }
        ]
    };

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    // ── OpenAPI ─────────────────────────────────────────────────────────────

    [Fact]
    public void OpenApiPathsAreTheRoutesTheApplicationServes()
    {
        var doc = Json(OpenApiExporter.ExportJson(Schema()));
        var paths = doc.GetProperty("paths");

        Assert.True(paths.TryGetProperty("/api/customers", out _));
        Assert.True(paths.TryGetProperty("/api/customers/{id}", out _));
        Assert.True(paths.TryGetProperty("/api/categories", out _));

        // What it used to emit, for every entity.
        Assert.False(paths.TryGetProperty("/api/v1/customer", out _));
    }

    [Fact]
    public void OpenApiDocumentsOnlyTheMethodsAnEntityExposes()
    {
        // Every operation was emitted for every entity regardless of what it declared, so the
        // specification promised DELETE on read-only resources.
        var doc = Json(OpenApiExporter.ExportJson(Schema()));
        var paths = doc.GetProperty("paths");

        var categories = paths.GetProperty("/api/categories");
        Assert.True(categories.TryGetProperty("get", out _));
        Assert.False(categories.TryGetProperty("post", out _));
        Assert.False(paths.TryGetProperty("/api/categories/{id}", out _));
    }

    [Fact]
    public void OpenApiIncludesPut()
    {
        // The API serves PUT and the specification never mentioned it, so an update was undocumented.
        var doc = Json(OpenApiExporter.ExportJson(Schema()));

        Assert.True(doc.GetProperty("paths").GetProperty("/api/customers/{id}")
            .TryGetProperty("put", out _));
    }

    [Fact]
    public void OpenApiOmitsAnEntityWithNoRestSurface()
    {
        var doc = Json(OpenApiExporter.ExportJson(Schema()));

        Assert.False(doc.GetProperty("paths").TryGetProperty("/api/auditrecords", out _));

        // Still described, because another schema may reference it.
        Assert.True(doc.GetProperty("components").GetProperty("schemas")
            .TryGetProperty("AuditRecord", out _));
    }

    [Fact]
    public void OpenApiIncludesCustomEndpoints()
    {
        var doc = Json(OpenApiExporter.ExportJson(Schema()));

        Assert.True(doc.GetProperty("paths").GetProperty("/api/orders/checkout")
            .TryGetProperty("post", out _));
    }

    [Fact]
    public void OpenApiDoesNotRewriteNamesThatContainItsOwnPlaceholders()
    {
        // The exporter serialised placeholder keys (_ref, _200, application_json) and repaired them
        // with string.Replace over the whole document, so any name containing one of those
        // substrings was silently rewritten with it.
        var schema = new SchemaModel
        {
            Namespace = "Test.Domain",
            Entities =
            [
                new Entity
                {
                    Name = "Report_200",
                    ApiEnabledMethods = ["GET"],
                    Properties =
                    [
                        new Property { Name = "Id", Type = "ObjectId", IsKey = true },
                        new Property { Name = "href_ref", Type = "string" }
                    ]
                }
            ]
        };

        var text = OpenApiExporter.ExportJson(schema);

        Assert.Contains("Report_200", text);
        Assert.Contains("href_ref", text);
        Assert.DoesNotContain("Report200", text);
    }

    [Fact]
    public void OpenApiIsValidJsonWithTheStructuralKeysItNeeds()
    {
        var doc = Json(OpenApiExporter.ExportJson(Schema()));

        Assert.Equal("3.1.0", doc.GetProperty("openapi").GetString());
        Assert.Equal("2.0.0", doc.GetProperty("info").GetProperty("version").GetString());

        var list = doc.GetProperty("paths").GetProperty("/api/customers").GetProperty("get");
        var items = list.GetProperty("responses").GetProperty("200")
            .GetProperty("content").GetProperty("application/json")
            .GetProperty("schema").GetProperty("items");

        Assert.Equal("#/components/schemas/Customer", items.GetProperty("$ref").GetString());
    }

    [Fact]
    public void EveryOpenApiPathIsAPathTheManifestDeclares()
    {
        // The check that would have caught this without anyone thinking to look: the manifest is what
        // the application serves, so a documented path that is not in it is a documented 404. On the
        // repository's own showcase schema the old exporter emitted six CRUD paths for entities that
        // declared no REST surface at all — wrong prefix and wrong existence, independently.
        var schema = Schema();
        var manifest = JsonDocument.Parse(ApiManifestGenerator.Generate(schema)).RootElement;

        var declared = manifest.GetProperty("Endpoints").EnumerateArray()
            .Select(e => e.GetProperty("Route").GetString()!)
            .SelectMany(route => new[] { route, $"{route}/{{id}}" })
            .Concat(manifest.GetProperty("CustomEndpoints").EnumerateArray()
                .Select(e => e.GetProperty("Route").GetString()!))
            .ToHashSet();

        var documented = Json(OpenApiExporter.ExportJson(schema))
            .GetProperty("paths").EnumerateObject().Select(p => p.Name);

        Assert.All(documented, path => Assert.Contains(path, declared));
    }

    // ── Postman ─────────────────────────────────────────────────────────────

    [Fact]
    public void PostmanRequestsPointAtTheRoutesTheApplicationServes()
    {
        var doc = Json(PostmanExporter.ExportJson(Schema()));

        var customer = doc.GetProperty("item").EnumerateArray()
            .Single(i => i.GetProperty("name").GetString() == "Customer");

        var urls = customer.GetProperty("item").EnumerateArray()
            .Select(r => r.GetProperty("request").GetProperty("url").GetProperty("raw").GetString())
            .ToList();

        Assert.Contains("{{baseUrl}}/api/customers", urls);
        Assert.DoesNotContain(urls, u => u!.Contains("/api/v1/"));
    }

    [Fact]
    public void PostmanBodiesAreShapedLikeTheEntity()
    {
        // Every POST body was the literal {"sampleField": "value"}, which no endpoint could accept.
        var doc = Json(PostmanExporter.ExportJson(Schema()));

        var create = doc.GetProperty("item").EnumerateArray()
            .Single(i => i.GetProperty("name").GetString() == "Customer")
            .GetProperty("item").EnumerateArray()
            .Single(r => r.GetProperty("name").GetString() == "Create Customer");

        var body = Json(create.GetProperty("request").GetProperty("body").GetProperty("raw").GetString()!);

        Assert.True(body.TryGetProperty("FullName", out _));
        Assert.True(body.TryGetProperty("CreditLimit", out _));

        // Server-assigned, so a caller never sends it.
        Assert.False(body.TryGetProperty("Id", out _));
    }

    [Fact]
    public void PostmanCoversEveryMethodTheEntityExposes()
    {
        var doc = Json(PostmanExporter.ExportJson(Schema()));

        var names = doc.GetProperty("item").EnumerateArray()
            .Single(i => i.GetProperty("name").GetString() == "Customer")
            .GetProperty("item").EnumerateArray()
            .Select(r => r.GetProperty("request").GetProperty("method").GetString())
            .ToList();

        Assert.Equal(["GET", "POST", "GET", "PUT", "DELETE"], names);
    }

    [Fact]
    public void PostmanSendsAnAuthorizationHeader()
    {
        // The generated endpoints refuse anonymous callers, so a collection without one is a
        // collection where every request comes back 401.
        var doc = Json(PostmanExporter.ExportJson(Schema()));

        var headers = doc.GetProperty("item").EnumerateArray().First()
            .GetProperty("item").EnumerateArray().First()
            .GetProperty("request").GetProperty("header").EnumerateArray()
            .Select(h => h.GetProperty("key").GetString())
            .ToList();

        Assert.Contains("Authorization", headers);
    }

    [Fact]
    public void PostmanOmitsAnEntityWithNoRestSurface()
    {
        var doc = Json(PostmanExporter.ExportJson(Schema()));

        var folders = doc.GetProperty("item").EnumerateArray()
            .Select(i => i.GetProperty("name").GetString()).ToList();

        Assert.DoesNotContain("AuditRecord", folders);
    }

    // ── AsyncAPI ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Order", "order-events")]
    [InlineData("PurchaseOrder", "purchase-order-events")]
    [InlineData("APIKey", "apikey-events")]
    public void AsyncApiTopicsMatchWhatTheDispatcherPublishesTo(string entity, string expected)
    {
        // KafkaOutboxDispatcher kebab-cases the event type; this lower-cased the whole name. A
        // single-word entity agreed by luck and every multi-word one named a topic with no publisher.
        Assert.Equal(expected, AsyncApiExporter.TopicFor(entity, null));
    }

    [Fact]
    public void AsyncApiUsesADeclaredTopicAsGiven()
    {
        Assert.Equal("orders.v2", AsyncApiExporter.TopicFor("Order", "orders.v2"));
    }

    [Fact]
    public void AsyncApiDocumentsBothDirectionsOnTheChannel()
    {
        var schema = new SchemaModel
        {
            Namespace = "Test.Domain",
            Entities =
            [
                new Entity
                {
                    Name = "PurchaseOrder",
                    KafkaOutboxEnabled = true,
                    Properties = [new Property { Name = "Id", Type = "ObjectId", IsKey = true }]
                }
            ]
        };

        var doc = Json(AsyncApiExporter.ExportJson(schema));

        Assert.Equal("purchase-order-events",
            doc.GetProperty("channels").GetProperty("purchase_order_events").GetProperty("address").GetString());

        Assert.Equal("receive",
            doc.GetProperty("operations").GetProperty("PurchaseOrderSubscribe").GetProperty("action").GetString());
        Assert.Equal("send",
            doc.GetProperty("operations").GetProperty("PurchaseOrderPublish").GetProperty("action").GetString());
    }

    [Fact]
    public void AsyncApiDoesNotRewriteNamesThatContainItsOwnPlaceholder()
    {
        var schema = new SchemaModel
        {
            Namespace = "Test.Domain",
            Entities =
            [
                new Entity
                {
                    Name = "Href_ref",
                    KafkaOutboxEnabled = true,
                    Properties = [new Property { Name = "Id", Type = "ObjectId", IsKey = true }]
                }
            ]
        };

        var text = AsyncApiExporter.ExportJson(schema);

        Assert.Contains("Href_ref", text);
        Assert.Contains("\"$ref\"", text);
    }

    // ── Mermaid ─────────────────────────────────────────────────────────────

    [Fact]
    public void MermaidEmitsAClassPerEntity()
    {
        var text = MermaidExporter.ExportMermaid(Schema());

        Assert.StartsWith("classDiagram", text.TrimStart());
        Assert.Contains("class Customer {", text);
        Assert.Contains("class Category {", text);
        Assert.Contains("FullName", text);
    }
}
