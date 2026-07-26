using System.ComponentModel.DataAnnotations;
using Foundry.Api.Endpoints;
using Microsoft.AspNetCore.Http;
using MongoDB.Bson;
using Xunit;

namespace Foundry.Api.Tests;

/// <summary>
/// Query-string filtering on generated list endpoints.
/// </summary>
/// <remarks>
/// The consequential direction here is *widening*. A filter the caller supplied and the server quietly
/// discarded produces a 200 with more rows than were asked for, and nothing anywhere records that a
/// filter was dropped. In a framework whose primary claim is tenant isolation, returning more than the
/// caller requested is the worst way to fail.
/// </remarks>
public class DynamicFilterTests
{
    private sealed class Order
    {
        public ObjectId Id { get; set; }
        public string Status { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public decimal Total { get; set; }
        public bool IsPaid { get; set; }
    }

    private static HttpContext ContextWith(params (string Key, string Value)[] query)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = QueryString.Create(
            query.Select(q => new KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues>(q.Key, q.Value)));
        return context;
    }

    // ---- values that filter correctly ----

    [Fact]
    public void NoQueryStringMeansNoFilter()
    {
        Assert.Null(DynamicEndpointRouteBuilder.BuildFilterExpression<Order>(ContextWith()));
    }

    [Fact]
    public void AStringValueBuildsAFilter()
    {
        var filter = DynamicEndpointRouteBuilder.BuildFilterExpression<Order>(ContextWith(("Status", "Draft")));

        Assert.NotNull(filter);
        var predicate = filter!.Compile();
        Assert.True(predicate(new Order { Status = "Draft" }));
        Assert.False(predicate(new Order { Status = "Shipped" }));
    }

    [Theory]
    [InlineData("Quantity", "5")]
    [InlineData("Total", "10.50")]
    [InlineData("IsPaid", "true")]
    public void TypedValuesBuildFilters(string key, string value)
    {
        Assert.NotNull(DynamicEndpointRouteBuilder.BuildFilterExpression<Order>(ContextWith((key, value))));
    }

    [Fact]
    public void PagingParametersAreNotTreatedAsFilters()
    {
        Assert.Null(DynamicEndpointRouteBuilder.BuildFilterExpression<Order>(
            ContextWith(("sortBy", "Status"), ("limit", "10"), ("sortOrder", "asc"))));
    }

    [Fact]
    public void AnUnknownParameterIsIgnored()
    {
        // A parameter that names no property cannot filter anything, and rejecting it would break
        // callers appending their own tracking parameters.
        Assert.Null(DynamicEndpointRouteBuilder.BuildFilterExpression<Order>(ContextWith(("utm_source", "email"))));
    }

    // ---- values that cannot filter ----

    [Theory]
    [InlineData("Quantity", "not-a-number")]
    [InlineData("Total", "abc")]
    [InlineData("IsPaid", "perhaps")]
    [InlineData("Id", "not-an-objectid")]
    public void AnUnparseableValueIsRejectedRatherThanDropped(string key, string value)
    {
        // Previously this was logged to Debug.WriteLine -- compiled out entirely in Release -- and then
        // skipped, so the filter silently vanished and the endpoint returned the unfiltered set.
        Assert.Throws<ValidationException>(
            () => DynamicEndpointRouteBuilder.BuildFilterExpression<Order>(ContextWith((key, value))));
    }

    [Fact]
    public void TheRejectionNamesTheParameterAndTheValue()
    {
        var error = Assert.Throws<ValidationException>(
            () => DynamicEndpointRouteBuilder.BuildFilterExpression<Order>(ContextWith(("Quantity", "many"))));

        Assert.Contains("Quantity", error.Message);
        Assert.Contains("many", error.Message);
    }

    [Fact]
    public void AnUnparseableValueDoesNotSilentlyWidenTheResultSet()
    {
        // The property that matters, stated directly: a request combining a valid filter with an
        // invalid one must not fall back to filtering on the valid one alone.
        Assert.Throws<ValidationException>(
            () => DynamicEndpointRouteBuilder.BuildFilterExpression<Order>(
                ContextWith(("Status", "Draft"), ("Quantity", "not-a-number"))));
    }
}
