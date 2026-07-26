using System.Net;
using Foundry.Connectors;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Foundry.Connectors.Tests;

/// <summary>
/// Failure reporting for the GraphQL and SOAP connectors.
/// </summary>
/// <remarks>
/// Both protocols report application-level failure in the response body rather than only in the
/// status code, so a connector that reads only the status code cannot tell success from failure.
/// </remarks>
public class GraphQLAndSoapTests
{
    private sealed record Reply(string Name);

    private sealed class StubHandler(HttpStatusCode status, string body, string contentType = "application/json")
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, contentType)
            });
    }

    private static GraphQLConnector GraphQL(HttpStatusCode status, string body)
        => new(
            new HttpClient(new StubHandler(status, body)) { BaseAddress = new Uri("https://api.example.com/") },
            new ConnectorOptions { Name = "catalog", BaseUrl = "https://api.example.com/" },
            NullLogger<GraphQLConnector>.Instance);

    private static SoapConnector Soap(HttpStatusCode status, string body)
        => new(
            new HttpClient(new StubHandler(status, body, "text/xml")) { BaseAddress = new Uri("https://legacy.example.com/") },
            new ConnectorOptions { Name = "legacy", BaseUrl = "https://legacy.example.com/" },
            NullLogger<SoapConnector>.Instance);

    // ---- GraphQL ----

    [Fact]
    public async Task GraphQLErrors_AreRaisedEvenThoughTheStatusIs200()
    {
        // The normal GraphQL failure mode. The connector returned null and dropped the errors array,
        // so a rejected query looked identical to one that legitimately matched nothing -- and the
        // server's explanation never reached the caller.
        var connector = GraphQL(
            HttpStatusCode.OK,
            """{"errors":[{"message":"Field 'nmae' does not exist on type 'Product'"}]}""");

        var error = await Assert.ThrowsAsync<HttpRequestException>(
            () => connector.ExecuteAsync<object, Reply>("{ product { nmae } }", new { }));

        Assert.Contains("does not exist", error.Message);
    }

    [Fact]
    public async Task GraphQLErrors_AreRaisedEvenWhenPartialDataIsPresent()
    {
        // A partial response carries both data and errors. Returning the data and silently dropping
        // the errors hands the caller a half-populated result it believes is complete.
        var connector = GraphQL(
            HttpStatusCode.OK,
            """{"data":{"name":"Widget"},"errors":[{"message":"price unavailable"}]}""");

        var error = await Assert.ThrowsAsync<HttpRequestException>(
            () => connector.ExecuteAsync<object, Reply>("{ product { name price } }", new { }));

        Assert.Contains("price unavailable", error.Message);
    }

    [Fact]
    public async Task GraphQLErrors_NameTheConnector()
    {
        var connector = GraphQL(HttpStatusCode.OK, """{"errors":[{"message":"boom"}]}""");

        var error = await Assert.ThrowsAsync<HttpRequestException>(
            () => connector.ExecuteAsync<object, Reply>("{ x }", new { }));

        Assert.Contains("catalog", error.Message);
    }

    [Fact]
    public async Task AnEmptyGraphQLErrorsArrayIsNotAFailure()
    {
        var connector = GraphQL(HttpStatusCode.OK, """{"data":{"name":"Widget"},"errors":[]}""");

        var reply = await connector.ExecuteAsync<object, Reply>("{ product { name } }", new { });

        Assert.Equal("Widget", reply!.Name);
    }

    [Fact]
    public async Task ASuccessfulGraphQLResponseReturnsData()
    {
        var connector = GraphQL(HttpStatusCode.OK, """{"data":{"name":"Widget"}}""");

        var reply = await connector.ExecuteAsync<object, Reply>("{ product { name } }", new { });

        Assert.Equal("Widget", reply!.Name);
    }

    [Fact]
    public async Task AGraphQLTransportFailureIncludesTheBody()
    {
        var connector = GraphQL(HttpStatusCode.BadGateway, "upstream unavailable");

        var error = await Assert.ThrowsAsync<HttpRequestException>(
            () => connector.ExecuteAsync<object, Reply>("{ x }", new { }));

        Assert.Contains("upstream unavailable", error.Message);
    }

    // ---- SOAP ----

    [Fact]
    public async Task ASoapFaultBodyReachesTheCaller()
    {
        // SOAP faults arrive as HTTP 500 with the detail in the body. Reporting only
        // "500 Internal Server Error" discards the entire description of the failure.
        const string fault = """
        <?xml version="1.0"?>
        <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
          <soap:Body><soap:Fault><faultstring>Customer 42 is not on file</faultstring></soap:Fault></soap:Body>
        </soap:Envelope>
        """;

        var connector = Soap(HttpStatusCode.InternalServerError, fault);

        var error = await Assert.ThrowsAsync<HttpRequestException>(
            () => connector.ExecuteAsync<string, Reply>("GetCustomer", "<id>42</id>"));

        Assert.Contains("not on file", error.Message);
    }

    [Fact]
    public async Task ASoapFailureNamesTheConnectorAndAction()
    {
        var connector = Soap(HttpStatusCode.InternalServerError, "fault");

        var error = await Assert.ThrowsAsync<HttpRequestException>(
            () => connector.ExecuteAsync<string, Reply>("GetCustomer", "<id>42</id>"));

        Assert.Contains("legacy", error.Message);
        Assert.Contains("GetCustomer", error.Message);
    }
}

/// <summary>
/// SOAP response deserialization failures.
/// </summary>
public class SoapDeserializationTests
{
    private sealed record Reply(string Name);

    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "text/xml"),
            });
    }

    private static SoapConnector Connector(string body) => new(
        new HttpClient(new StubHandler(body)) { BaseAddress = new Uri("https://legacy.example.com/") },
        new ConnectorOptions { Name = "legacy", BaseUrl = "https://legacy.example.com/" },
        Microsoft.Extensions.Logging.Abstractions.NullLogger<SoapConnector>.Instance);

    [Fact]
    public async Task AnUnparseableResponseIsReportedRatherThanReturnedAsNothing()
    {
        // `catch { return default; }` made a schema mismatch on the remote side indistinguishable from
        // a legitimately empty result, so an integration that had silently broken looked like a
        // service with no data.
        var connector = Connector("<html><body>Gateway timeout</body></html>");

        var error = await Assert.ThrowsAsync<HttpRequestException>(
            () => connector.ExecuteAsync<string, Reply>("GetCustomer", "<id>42</id>"));

        Assert.Contains("could not be deserialized", error.Message);
    }

    [Fact]
    public async Task TheFailureNamesTheConnectorAndTheExpectedType()
    {
        var connector = Connector("<html/>");

        var error = await Assert.ThrowsAsync<HttpRequestException>(
            () => connector.ExecuteAsync<string, Reply>("GetCustomer", "<id>42</id>"));

        Assert.Contains("legacy", error.Message);
        Assert.Contains("Reply", error.Message);
    }

    [Fact]
    public async Task TheResponseBodyIsIncludedForDiagnosis()
    {
        // For SOAP the envelope is the only description of what actually came back.
        var connector = Connector("<html><body>Gateway timeout</body></html>");

        var error = await Assert.ThrowsAsync<HttpRequestException>(
            () => connector.ExecuteAsync<string, Reply>("GetCustomer", "<id>42</id>"));

        Assert.Contains("Gateway timeout", error.Message);
    }
}
