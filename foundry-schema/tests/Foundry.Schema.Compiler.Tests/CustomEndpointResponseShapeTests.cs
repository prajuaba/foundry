using System.Collections.Generic;
using Foundry.Schema.Compiler;
using Xunit;

namespace Foundry.Schema.Compiler.Tests;

/// <summary>
/// A custom GET endpoint may answer with a page -- its rows plus the total that matched -- rather
/// than a bare list, so a caller can tell a clamped answer from a whole one.
/// </summary>
public class CustomEndpointResponseShapeTests
{
    private static CustomEndpoint Lookup(string? shape, string method = "GET", string? mediaType = null) => new()
    {
        Route = "/api/v1/orders/lookup",
        Method = method,
        RequestType = "OrderLookupQuery",
        OperationType = "Query",
        TargetEntity = "Order",
        FilterField = "CustomerId",
        FilterSourceValue = "CustomerId",
        ResponseShape = shape,
        ResponseMediaType = mediaType
    };

    private static SchemaModel SchemaWith(CustomEndpoint endpoint) => new()
    {
        Namespace = "Sales.Contracts",
        Entities = new List<Entity>
        {
            new Entity
            {
                Name = "Order",
                Properties = new List<Property>
                {
                    new Property { Name = "Id", Type = "ObjectId", IsKey = true },
                    new Property { Name = "CustomerId", Type = "string" }
                }
            }
        },
        CustomEndpoints = new List<CustomEndpoint> { endpoint }
    };

    [Fact]
    public void APageEndpointAnswersWithAPagedResult()
    {
        var generated = PocoGenerator.Generate(SchemaWith(Lookup("Page")));

        Assert.Contains("IRequest<Foundry.Core.Paging.PagedResult<Order>>", generated["Commands/OrderLookupQuery"]);
    }

    [Fact]
    public void APageEndpointsScaffoldCountsWithTheSameFilterAndReturnsTheTotal()
    {
        var code = TestHelpers.GenerateForSingleCustomEndpoint(Lookup("Page"), "Sales.Contracts");

        Assert.Contains("IRequestHandler<OrderLookupQuery, Foundry.Core.Paging.PagedResult<Order>>", code);
        Assert.Contains("var total = await _repository.CountAsync(", code);
        Assert.Contains("return Foundry.Core.Paging.PagedResult<Order>.From(items, total, 1, items.Count);", code);
        Assert.DoesNotContain("return items;", code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("List")]
    public void AnEndpointThatDoesNotAskForAPageIsUnchanged(string? shape)
    {
        var generated = PocoGenerator.Generate(SchemaWith(Lookup(shape)));

        Assert.Contains("IRequest<System.Collections.Generic.IReadOnlyList<Order>>", generated["Commands/OrderLookupQuery"]);
        Assert.False(SchemaValidator.Validate(SchemaWith(Lookup(shape))).HasErrors);
    }

    [Fact]
    public void APageEndpointValidates()
    {
        Assert.False(SchemaValidator.Validate(SchemaWith(Lookup("Page"))).HasErrors);
    }

    [Theory]
    [InlineData("Paged", "GET", null)]
    [InlineData("Page", "POST", null)]
    [InlineData("Page", "GET", "text/csv")]
    public void AShapeThatCannotApplyIsRefusedWithFDY2017(string shape, string method, string? mediaType)
    {
        // An unknown name, a page of a command's single result, and a page of a file all have no
        // JSON rows to page.
        var diagnostics = SchemaValidator.Validate(SchemaWith(Lookup(shape, method, mediaType)));

        Assert.Contains(diagnostics.Items, d => d.Code == DiagnosticCatalog.EndpointUnsupportedResponseShape);
        Assert.True(diagnostics.HasErrors);
    }
}
