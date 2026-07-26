using System.Net;
using Foundry.Connectors;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Foundry.Connectors.Tests;

/// <summary>
/// Request shaping and failure reporting in <see cref="RestConnector"/>.
/// </summary>
public class RestConnectorRequestTests
{
    private sealed record Payload(string Name);
    private sealed record Reply(string Status);

    /// <summary>Captures the outgoing request and returns a canned response.</summary>
    private sealed class StubHandler(HttpStatusCode status = HttpStatusCode.OK, string body = """{"status":"ok"}""")
        : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }

    private static (RestConnector Connector, StubHandler Handler) Build(
        HttpStatusCode status = HttpStatusCode.OK,
        string body = """{"status":"ok"}""",
        Action<ConnectorOptions>? configure = null)
    {
        var handler = new StubHandler(status, body);
        var options = new ConnectorOptions { Name = "test", BaseUrl = "https://api.example.com/" };
        configure?.Invoke(options);

        var client = new HttpClient(handler);
        return (new RestConnector(client, options, NullLogger<RestConnector>.Instance), handler);
    }

    // ---- verb selection ----

    [Fact]
    public async Task APayloadIsSentAsAPost()
    {
        var (connector, handler) = Build();

        await connector.ExecuteAsync<Payload, Reply>("things", new Payload("Ada"));

        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
    }

    [Fact]
    public async Task ANullPayloadIsSentAsAGet()
    {
        var (connector, handler) = Build();

        await connector.ExecuteAsync<Payload?, Reply>("things", null);

        Assert.Equal(HttpMethod.Get, handler.LastRequest!.Method);
    }

    [Fact]
    public async Task AZeroValuedPayloadIsStillSentAsAPost()
    {
        // The verb was chosen with payload.Equals(default(TRequest)), so a legitimate value that
        // happens to equal its type's default -- 0 for an int, false for a bool -- was mistaken for
        // "no payload" and silently downgraded to a GET, dropping the body.
        var (connector, handler) = Build();

        await connector.ExecuteAsync<int, Reply>("things", 0);

        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
    }

    [Fact]
    public async Task AFalsePayloadIsStillSentAsAPost()
    {
        var (connector, handler) = Build();

        await connector.ExecuteAsync<bool, Reply>("things", false);

        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
    }

    // ---- failure reporting ----

    [Fact]
    public async Task AFailureResponseIncludesTheRemoteBody()
    {
        // EnsureSuccessStatusCode throws with only the status code, so the remote service's
        // explanation -- the one thing that makes an integration failure diagnosable -- was
        // discarded. For a connector whose whole job is calling someone else's API, that turns every
        // 4xx into a guessing exercise.
        var (connector, _) = Build(
            HttpStatusCode.BadRequest,
            """{"error":"customerId is required"}""");

        var error = await Assert.ThrowsAsync<HttpRequestException>(
            () => connector.ExecuteAsync<Payload, Reply>("things", new Payload("Ada")));

        Assert.Contains("customerId is required", error.Message);
    }

    [Fact]
    public async Task AFailureResponseNamesTheConnectorAndStatus()
    {
        var (connector, _) = Build(HttpStatusCode.Forbidden, "nope");

        var error = await Assert.ThrowsAsync<HttpRequestException>(
            () => connector.ExecuteAsync<Payload, Reply>("things", new Payload("Ada")));

        Assert.Contains("test", error.Message);
        Assert.Contains("403", error.Message);
    }

    [Fact]
    public async Task ASuccessfulResponseIsDeserialized()
    {
        var (connector, _) = Build(body: """{"status":"created"}""");

        var reply = await connector.ExecuteAsync<Payload, Reply>("things", new Payload("Ada"));

        Assert.Equal("created", reply!.Status);
    }

    // ---- authentication ----

    [Fact]
    public async Task ABearerTokenIsSent()
    {
        var (connector, handler) = Build(configure: o =>
        {
            o.AuthType = AuthenticationType.Bearer;
            o.Token = "abc123";
        });

        await connector.ExecuteAsync<Payload?, Reply>("things", null);

        Assert.Equal("Bearer", handler.LastRequest!.Headers.Authorization?.Scheme);
        Assert.Equal("abc123", handler.LastRequest.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task BasicCredentialsAreBase64Encoded()
    {
        var (connector, handler) = Build(configure: o =>
        {
            o.AuthType = AuthenticationType.Basic;
            o.Username = "ada";
            o.Password = "secret";
        });

        await connector.ExecuteAsync<Payload?, Reply>("things", null);

        var expected = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("ada:secret"));
        Assert.Equal(expected, handler.LastRequest!.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task ACustomApiKeyHeaderNameIsHonoured()
    {
        var (connector, handler) = Build(configure: o =>
        {
            o.AuthType = AuthenticationType.ApiKey;
            o.ApiKey = "key-1";
            o.ApiKeyHeaderName = "X-Tenant-Key";
        });

        await connector.ExecuteAsync<Payload?, Reply>("things", null);

        Assert.True(handler.LastRequest!.Headers.TryGetValues("X-Tenant-Key", out var values));
        Assert.Equal(["key-1"], values!);
    }

    [Fact]
    public async Task CustomHeadersAreSent()
    {
        var (connector, handler) = Build(configure: o => o.Headers["X-Trace"] = "abc");

        await connector.ExecuteAsync<Payload?, Reply>("things", null);

        Assert.True(handler.LastRequest!.Headers.TryGetValues("X-Trace", out _));
    }

    // ---- health ----

    [Fact]
    public async Task HealthIsTrueOnASuccessfulResponse()
    {
        var (connector, _) = Build();
        Assert.True(await connector.CheckHealthAsync());
    }

    [Fact]
    public async Task HealthIsFalseOnAFailureResponse()
    {
        var (connector, _) = Build(HttpStatusCode.ServiceUnavailable, "down");
        Assert.False(await connector.CheckHealthAsync());
    }

    [Fact]
    public void ConstructorArgumentsAreValidated()
    {
        var options = new ConnectorOptions { Name = "test" };

        Assert.Throws<ArgumentNullException>(() => new RestConnector(null!, options, NullLogger<RestConnector>.Instance));
        Assert.Throws<ArgumentNullException>(() => new RestConnector(new HttpClient(), null!, NullLogger<RestConnector>.Instance));
        Assert.Throws<ArgumentNullException>(() => new RestConnector(new HttpClient(), options, null!));
    }
}
