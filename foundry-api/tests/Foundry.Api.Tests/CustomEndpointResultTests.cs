using Foundry.Api.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Xunit;

namespace Foundry.Api.Tests;

/// <summary>
/// One API answered a missing id two different ways.
/// </summary>
/// <remarks>
/// The route generator emitted <c>if (result == null) return Results.NoContent();</c> for every
/// custom endpoint, whatever the handler returned. Foundry's own scaffold for a custom update
/// handler writes <c>if (entity == null) return false;</c> — so <c>false</c> is the framework's way
/// of saying "no such record" — and the endpoint layer serialized that <c>false</c> with a
/// <c>200 OK</c>, while generated CRUD returned 404 for the very same missing id. Any client
/// checking status rather than parsing the body read a failed update as a successful one.
/// </remarks>
public class CustomEndpointResultTests
{
    [Fact]
    public void FalseIsNotFound()
    {
        // The whole point: the scaffold's not-found convention has to reach the wire.
        Assert.IsType<NotFound>(CustomEndpointResult.From(false));
    }

    [Fact]
    public void TrueIsOk()
    {
        var result = CustomEndpointResult.From(true);

        var ok = Assert.IsType<Ok<bool>>(result);
        Assert.True(ok.Value);
    }

    [Fact]
    public void ANullPayloadIsNoContent()
    {
        Assert.IsType<NoContent>(CustomEndpointResult.From<string?>(null));
    }

    [Fact]
    public void APayloadIsSerialized()
    {
        var rows = new[] { new { Name = "first" }, new { Name = "second" } };

        // Not Ok<T>: the generator serializes with Foundry's own options so ObjectId round-trips.
        Assert.IsAssignableFrom<IResult>(CustomEndpointResult.From(rows));
        Assert.IsNotType<NotFound>(CustomEndpointResult.From(rows));
        Assert.IsNotType<NoContent>(CustomEndpointResult.From(rows));
    }

    [Fact]
    public void AnEmptyCollectionIsStillASuccessfulAnswer()
    {
        // A query that matched nothing is a 200 with [], not a 404. Only a bare `false` means
        // not-found, because only the update scaffolds return one.
        var result = CustomEndpointResult.From(Array.Empty<string>());

        Assert.IsNotType<NotFound>(result);
        Assert.IsNotType<NoContent>(result);
    }

    [Fact]
    public void UnitIsNotMistakenForNotFound()
    {
        // Workflow transition commands return MediatR.Unit. It is a struct, so it is never null and
        // never false -- and it must not be swept into the not-found branch.
        var result = CustomEndpointResult.From(global::MediatR.Unit.Value);

        Assert.IsNotType<NotFound>(result);
        Assert.IsNotType<NoContent>(result);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(42)]
    public void AZeroValuedStructIsNotNotFound(int value)
    {
        // `result == null` on a value type was the dead comparison that started this. Zero is a
        // legitimate answer -- a count of nothing -- not a missing record.
        Assert.IsNotType<NotFound>(CustomEndpointResult.From(value));
    }
}
