using System.Net;
using System.Text;
using Foundry.Connectors;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Foundry.Connectors.Tests;

/// <summary>
/// What the REST connector does with a remote response it did not expect.
/// </summary>
/// <remarks>
/// The existing suite covers how a request is *shaped* and how a failure status is reported. This
/// covers the other direction: a remote service that answers 200 with something the connector cannot
/// use. That is not an edge case for an integration component — it is what a misconfigured endpoint,
/// a captive portal, an expired session redirect or a partial outage all look like.
/// </remarks>
public class RestResponseHandlingTests
{
    private sealed record Reply(string Name, int Quantity);

    private sealed class StubHandler(HttpStatusCode status, string body, string contentType)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var message = new HttpResponseMessage(status);

            if (body.Length > 0 || contentType.Length > 0)
            {
                message.Content = new StringContent(body, Encoding.UTF8, contentType);
            }
            else
            {
                message.Content = new StringContent(string.Empty);
                message.Content.Headers.ContentType = null;
            }

            return Task.FromResult(message);
        }
    }

    private static RestConnector Connector(
        string body,
        HttpStatusCode status = HttpStatusCode.OK,
        string contentType = "application/json")
        => new(
            new HttpClient(new StubHandler(status, body, contentType))
            {
                BaseAddress = new Uri("https://api.example.com/")
            },
            new ConnectorOptions { Name = "catalog", BaseUrl = "https://api.example.com/" },
            NullLogger<RestConnector>.Instance);

    [Fact]
    public async Task AWellFormedResponseIsReturned()
    {
        var reply = await Connector("""{"name":"Widget","quantity":3}""")
            .ExecuteAsync<object, Reply>("products", new { });

        Assert.Equal("Widget", reply!.Name);
        Assert.Equal(3, reply.Quantity);
    }

    [Fact]
    public async Task AnHtmlPageReturnedWithA200IsRejected()
    {
        // A login page, a captive portal or a proxy error served with a 200. The status says success
        // and the body is not what was asked for; returning null here would be reported to the caller
        // as "the service has no such product".
        var connector = Connector("<html><body>Sign in to continue</body></html>", contentType: "text/html");

        await Assert.ThrowsAnyAsync<Exception>(
            () => connector.ExecuteAsync<object, Reply>("products", new { }));
    }

    [Fact]
    public async Task MalformedJsonIsRejected()
    {
        var connector = Connector("""{"name":"Widget",""");

        await Assert.ThrowsAnyAsync<Exception>(
            () => connector.ExecuteAsync<object, Reply>("products", new { }));
    }

    [Fact]
    public async Task AFailureStatusCarriesTheRemoteBody()
    {
        // For a component whose whole purpose is calling someone else's API, the remote body is the
        // one thing that makes a failure diagnosable.
        var connector = Connector(
            """{"error":"product_not_found","detail":"sku 42 is retired"}""",
            HttpStatusCode.NotFound);

        var error = await Assert.ThrowsAsync<HttpRequestException>(
            () => connector.ExecuteAsync<object, Reply>("products/42", new { }));

        Assert.Contains("sku 42 is retired", error.Message);
        Assert.Contains("catalog", error.Message);
        Assert.Equal(HttpStatusCode.NotFound, error.StatusCode);
    }

    [Fact]
    public async Task AVeryLargeErrorBodyIsTruncated()
    {
        // A 500 answered with a full HTML page should not put a megabyte into an exception message,
        // a log line and every downstream alert.
        var connector = Connector(new string('x', 50_000), HttpStatusCode.InternalServerError);

        var error = await Assert.ThrowsAsync<HttpRequestException>(
            () => connector.ExecuteAsync<object, Reply>("products", new { }));

        Assert.Contains("truncated", error.Message);
        Assert.True(error.Message.Length < 5_000, $"error message was {error.Message.Length} characters");
    }

    [Fact]
    public async Task AJsonNullBodyIsReturnedAsNull()
    {
        // Documented rather than defended: a service answering `null` is saying "nothing here", and
        // the connector's return type is nullable to express exactly that.
        var reply = await Connector("null").ExecuteAsync<object, Reply>("products", new { });

        Assert.Null(reply);
    }
}
