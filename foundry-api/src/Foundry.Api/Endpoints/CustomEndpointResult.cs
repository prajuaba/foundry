using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Foundry.Api.Endpoints;

/// <summary>
/// Turns a custom endpoint handler's return value into an HTTP response.
/// </summary>
/// <remarks>
/// <para>
/// The route generator used to emit <c>if (result == null) return Results.NoContent();</c> for every
/// custom endpoint, whatever the handler returned. The generated update scaffolds return
/// <see cref="bool"/> and the workflow transition commands return <c>MediatR.Unit</c>, and neither is
/// ever <c>null</c> — so the check was dead code. The compiler said so, 50 times over, as
/// <c>CS0472</c> and <c>CS8073</c> in generated source the author never sees.
/// </para>
/// <para>
/// The consequence was not merely noise. Foundry's own scaffold for a custom update handler writes
/// <c>if (entity == null) return false;</c> — <c>false</c> is the framework's way of saying "no such
/// record" — and the endpoint layer then serialized that <c>false</c> with a <c>200 OK</c>. One API
/// answered a missing id two different ways: generated CRUD returned 404, and a custom update on the
/// same missing id returned 200 with a body of <c>false</c>. Any client checking status rather than
/// parsing the body treated a failed update as a successful one.
/// </para>
/// <para>
/// Mapping <c>false</c> to 404 here is what makes the scaffold's own convention reach the wire. A
/// handler that needs <c>false</c> to mean something other than "not found" should return a payload
/// type that says so, rather than a bare boolean.
/// </para>
/// </remarks>
public static class CustomEndpointResult
{
    /// <summary>
    /// Maps <paramref name="result"/> to a response: <c>false</c> to 404, <c>true</c> to 200,
    /// <c>null</c> to 204, and anything else to 200 with the value as JSON.
    /// </summary>
    public static IResult From<T>(T result)
    {
        // Checked before the null test: `is bool` cannot match null, and ordering it first keeps
        // the not-found mapping obvious.
        if (result is bool succeeded)
        {
            return succeeded ? Results.Ok(true) : Results.NotFound();
        }

        // `is null` rather than `== null`, which is what produced CS0472/CS8073 when T was a value
        // type. This form is legal for every T and simply never matches for non-nullable ones.
        if (result is null)
        {
            return Results.NoContent();
        }

        return Results.Text(
            JsonSerializer.Serialize(result, Foundry.Core.Serialization.FoundryJsonDefaults.Options),
            "application/json");
    }
}
