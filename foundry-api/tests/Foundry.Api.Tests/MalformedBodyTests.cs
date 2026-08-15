using System.Text.Json;
using Foundry.Api.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Foundry.Api.Tests;

/// <summary>
/// The two halves of the same mistake used to answer differently.
/// </summary>
/// <remarks>
/// <para>
/// Model binding runs before any Foundry code does, so a POST omitting a required property threw
/// <see cref="BadHttpRequestException"/> out of <c>System.Text.Json</c> and fell through to the
/// catch-all 500. A malformed value in the <em>query string</em> was correctly rejected with 400 by
/// the DataAnnotations branch — so <c>?priority=nonsense</c> was a client error and the identical
/// error in the body was reported as a server fault.
/// </para>
/// <para>
/// A caller could not tell "you sent something I cannot read" from "I am broken", and a 500 invites
/// a retry that can never succeed.
/// </para>
/// </remarks>
public class MalformedBodyTests
{
    private static async Task<(int Status, JsonElement Body)> HandleAsync(Exception exception)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/invoices";
        var responseBody = new MemoryStream();
        context.Response.Body = responseBody;

        var handled = await new GlobalExceptionHandler()
            .TryHandleAsync(context, exception, CancellationToken.None);

        Assert.True(handled, "The handler did not claim the exception, so it would surface as a 500.");

        responseBody.Position = 0;
        using var document = JsonDocument.Parse(responseBody);
        return (context.Response.StatusCode, document.RootElement.Clone());
    }

    private static BadHttpRequestException MissingRequiredProperties(params string[] names)
    {
        // The shape System.Text.Json produces, reproduced rather than provoked so the test does not
        // depend on a live model-binding pipeline.
        var quoted = string.Join(", ", names.Select(n => $"'{n}'"));
        var inner = new JsonException(
            $"JSON deserialization for type 'Contoso.Orders.Api.Domain.Invoice' was missing "
            + $"required properties including: {quoted}.");

        return new BadHttpRequestException(
            "Failed to read parameter \"Invoice entity\" from the request body as JSON.",
            StatusCodes.Status400BadRequest,
            inner);
    }

    [Fact]
    public async Task AMissingRequiredPropertyIsABadRequest()
    {
        var (status, _) = await HandleAsync(MissingRequiredProperties("primaryRole"));

        Assert.Equal(StatusCodes.Status400BadRequest, status);
    }

    [Fact]
    public async Task TheResponseNamesTheMissingProperties()
    {
        // The runtime already worked out which fields were absent. Answering 400 without saying
        // which one leaves the caller to guess at a shape the server knows exactly.
        var (_, body) = await HandleAsync(MissingRequiredProperties("primaryRole", "hireDate"));

        var missing = body.GetProperty("missingProperties")
            .EnumerateArray().Select(e => e.GetString()).ToList();

        Assert.Equal(new[] { "primaryRole", "hireDate" }, missing);
    }

    [Fact]
    public async Task TheApplicationTypeNameIsNotEchoedBack()
    {
        // The BCL message names the CLR type it was binding. That is the application's internal
        // namespace and it does the caller no good, so the detail is written here rather than
        // forwarded.
        var (_, body) = await HandleAsync(MissingRequiredProperties("primaryRole"));

        var serialized = body.GetRawText();

        Assert.DoesNotContain("Contoso.Orders.Api.Domain", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("JSON deserialization for type", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheJsonPathToTheOffendingMemberIsReported()
    {
        // System.Text.Json's own structured locator: no parsing, and it leaks nothing beyond the
        // document the caller sent.
        var inner = new JsonException(
            "The JSON value could not be converted to System.Int32.",
            path: "$.lineItems[2].quantity",
            lineNumber: 4,
            bytePositionInLine: 21);
        var exception = new BadHttpRequestException(
            "Failed to read parameter.", StatusCodes.Status400BadRequest, inner);

        var (status, body) = await HandleAsync(exception);

        Assert.Equal(StatusCodes.Status400BadRequest, status);
        Assert.Equal("$.lineItems[2].quantity", body.GetProperty("path").GetString());
    }

    [Fact]
    public async Task ABodyOverTheSizeLimitKeepsItsOwnStatus()
    {
        // The same exception type carries 413 for a payload above the configured limit. Answering
        // 400 there would be a second wrong code, so the exception's own status is used.
        var exception = new BadHttpRequestException(
            "Request body too large.", StatusCodes.Status413PayloadTooLarge);

        var (status, _) = await HandleAsync(exception);

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, status);
    }

    [Fact]
    public async Task AStatusOutsideTheClientErrorRangeFallsBackTo400()
    {
        // Defensive: this type's StatusCode is settable, and a 5xx on it would otherwise let the
        // handler report a client mistake as a server fault -- the exact defect being fixed.
        var exception = new BadHttpRequestException("odd", StatusCodes.Status500InternalServerError);

        var (status, _) = await HandleAsync(exception);

        Assert.Equal(StatusCodes.Status400BadRequest, status);
    }

    [Fact]
    public async Task AnUnrecognisedInnerMessageStillAnswers400()
    {
        // Reading a BCL message is guarded: if a future runtime rewords it the caller loses the
        // hint and still gets the correct status, which is the only failure this is allowed.
        var inner = new JsonException("something entirely reworded by a future runtime");
        var exception = new BadHttpRequestException("Failed to read parameter.", 400, inner);

        var (status, body) = await HandleAsync(exception);

        Assert.Equal(StatusCodes.Status400BadRequest, status);
        Assert.False(body.TryGetProperty("missingProperties", out _));
    }

    [Fact]
    public async Task InvalidJsonWithNoInnerDetailIsStillABadRequest()
    {
        var (status, body) = await HandleAsync(
            new BadHttpRequestException("Failed to read parameter.", 400));

        Assert.Equal(StatusCodes.Status400BadRequest, status);
        Assert.Equal("Malformed Request Body", body.GetProperty("title").GetString());
    }

    [Fact]
    public async Task AQueryStringAndABodyMistakeNowAgree()
    {
        // The point of the fix, stated as one assertion: the same class of error reported through
        // the two different binders lands on the same class of status.
        var (queryStatus, _) = await HandleAsync(
            new System.ComponentModel.DataAnnotations.ValidationException("priority was unreadable"));
        var (bodyStatus, _) = await HandleAsync(MissingRequiredProperties("priority"));

        Assert.Equal(StatusCodes.Status400BadRequest, queryStatus);
        Assert.Equal(queryStatus, bodyStatus);
    }

    [Fact]
    public async Task AnUnrelatedExceptionIsStillLeftAlone()
    {
        // The handler must keep declining what it does not understand, so genuine faults keep
        // surfacing as 500 rather than being relabelled as the caller's fault.
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        var handled = await new GlobalExceptionHandler()
            .TryHandleAsync(context, new InvalidOperationException("a real fault"), CancellationToken.None);

        Assert.False(handled);
    }
}
