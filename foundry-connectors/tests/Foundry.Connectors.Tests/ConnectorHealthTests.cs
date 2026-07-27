using System.Net;
using Foundry.Connectors;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Foundry.Connectors.Tests;

/// <summary>
/// Connector health checks, across all three protocols.
/// </summary>
/// <remarks>
/// Only the REST connector's health check was covered. A health check is what an operator reads at
/// three in the morning, so the direction that matters is a check reporting <em>healthy</em> for a
/// service that cannot serve a request — an outage that the dashboard says is not happening.
/// </remarks>
public class ConnectorHealthTests
{
    private sealed record Reply(string Name);

    private sealed class StubHandler(HttpStatusCode status, string body, string contentType = "application/json")
        : HttpMessageHandler
    {
        public string? LastPath { get; private set; }
        public HttpMethod? LastMethod { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastPath = request.RequestUri?.PathAndQuery;
            LastMethod = request.Method;

            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, contentType)
            });
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new HttpRequestException("no route to host");
    }

    private static HttpClient Client(HttpMessageHandler handler)
        => new(handler) { BaseAddress = new Uri("https://api.example.com/") };

    private static ConnectorOptions Options(string name) =>
        new() { Name = name, BaseUrl = "https://api.example.com/" };

    // ── REST ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(HttpStatusCode.OK, true)]
    [InlineData(HttpStatusCode.NoContent, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, false)]
    [InlineData(HttpStatusCode.InternalServerError, false)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    public async Task RestHealthFollowsTheStatusCode(HttpStatusCode status, bool expected)
    {
        var connector = new RestConnector(
            Client(new StubHandler(status, "{}")), Options("catalog"), NullLogger<RestConnector>.Instance);

        Assert.Equal(expected, await connector.CheckHealthAsync());
    }

    [Fact]
    public async Task RestHealthIsFalseWhenTheHostIsUnreachable()
    {
        // An unreachable host must read as unhealthy, not propagate. A health check that throws takes
        // the whole health endpoint with it, so one dead dependency reports every dependency as down.
        var connector = new RestConnector(
            Client(new ThrowingHandler()), Options("catalog"), NullLogger<RestConnector>.Instance);

        Assert.False(await connector.CheckHealthAsync());
    }

    // ── SOAP ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SoapHealthRequestsTheWsdl()
    {
        // The WSDL is the only endpoint a SOAP service is guaranteed to answer without a valid
        // envelope, so it is the only sound liveness probe for one.
        var handler = new StubHandler(HttpStatusCode.OK, "<definitions/>", "text/xml");
        var connector = new SoapConnector(Client(handler), Options("legacy"), NullLogger<SoapConnector>.Instance);

        Assert.True(await connector.CheckHealthAsync());
        Assert.Contains("wsdl", handler.LastPath);
        Assert.Equal(HttpMethod.Get, handler.LastMethod);
    }

    [Fact]
    public async Task SoapHealthIsFalseOnAFailureStatus()
    {
        var connector = new SoapConnector(
            Client(new StubHandler(HttpStatusCode.ServiceUnavailable, "down", "text/plain")),
            Options("legacy"), NullLogger<SoapConnector>.Instance);

        Assert.False(await connector.CheckHealthAsync());
    }

    [Fact]
    public async Task SoapHealthIsFalseWhenTheHostIsUnreachable()
    {
        var connector = new SoapConnector(
            Client(new ThrowingHandler()), Options("legacy"), NullLogger<SoapConnector>.Instance);

        Assert.False(await connector.CheckHealthAsync());
    }

    // ── GraphQL ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task GraphQlHealthPostsAnIntrospectionQuery()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"data":{"__typename":"Query"}}""");
        var connector = new GraphQLConnector(Client(handler), Options("catalog"), NullLogger<GraphQLConnector>.Instance);

        Assert.True(await connector.CheckHealthAsync());
        Assert.Equal(HttpMethod.Post, handler.LastMethod);
    }

    [Fact]
    public async Task GraphQlHealthIsFalseOnAFailureStatus()
    {
        var connector = new GraphQLConnector(
            Client(new StubHandler(HttpStatusCode.BadGateway, "upstream unavailable")),
            Options("catalog"), NullLogger<GraphQLConnector>.Instance);

        Assert.False(await connector.CheckHealthAsync());
    }

    [Fact]
    public async Task GraphQlHealthIsFalseWhenTheServerRejectsTheProbe()
    {
        // The GraphQL failure mode this codebase has already been bitten by once, in ExecuteAsync: a
        // server answers 200 and reports the failure in an `errors` array. A health check reading only
        // the status code calls a server healthy when it is rejecting every query it receives.
        var connector = new GraphQLConnector(
            Client(new StubHandler(HttpStatusCode.OK, """{"errors":[{"message":"schema unavailable"}]}""")),
            Options("catalog"), NullLogger<GraphQLConnector>.Instance);

        Assert.False(await connector.CheckHealthAsync());
    }

    [Fact]
    public async Task GraphQlHealthIsFalseWhenTheHostIsUnreachable()
    {
        var connector = new GraphQLConnector(
            Client(new ThrowingHandler()), Options("catalog"), NullLogger<GraphQLConnector>.Instance);

        Assert.False(await connector.CheckHealthAsync());
    }

    // ── The IHealthCheck wrapper an operator actually reads ─────────────────

    private sealed class FixedHealthConnector(bool healthy) : IFoundryConnector
    {
        public string Name => "catalog";
        public ConnectorType Type => ConnectorType.REST;
        public Task<bool> CheckHealthAsync(CancellationToken ct = default) => Task.FromResult(healthy);
        public Task<TResponse?> ExecuteAsync<TRequest, TResponse>(string e, TRequest p, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class ThrowingHealthConnector : IFoundryConnector
    {
        public string Name => "catalog";
        public ConnectorType Type => ConnectorType.REST;
        public Task<bool> CheckHealthAsync(CancellationToken ct = default)
            => throw new InvalidOperationException("connector misconfigured");
        public Task<TResponse?> ExecuteAsync<TRequest, TResponse>(string e, TRequest p, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    [Fact]
    public async Task TheHealthCheckNamesTheConnectorInBothDirections()
    {
        var context = new HealthCheckContext();

        var healthy = await new FoundryConnectorHealthCheck(new FixedHealthConnector(true))
            .CheckHealthAsync(context);
        var unhealthy = await new FoundryConnectorHealthCheck(new FixedHealthConnector(false))
            .CheckHealthAsync(context);

        Assert.Equal(HealthStatus.Healthy, healthy.Status);
        Assert.Equal(HealthStatus.Unhealthy, unhealthy.Status);
        Assert.Contains("catalog", unhealthy.Description);
    }

    [Fact]
    public async Task AConnectorThatThrowsIsReportedUnhealthyRatherThanBreakingTheEndpoint()
    {
        // The wrapper called through without guarding. A connector whose own check throws -- a
        // malformed base URL, a DNS failure surfacing as something other than HttpRequestException --
        // therefore took down the entire /health endpoint, so one broken dependency reported every
        // dependency as unknown.
        var result = await new FoundryConnectorHealthCheck(new ThrowingHealthConnector())
            .CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("catalog", result.Description);
        Assert.NotNull(result.Exception);
    }
}
