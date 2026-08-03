using Foundry.Core.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Foundry.Connectors.Tests;

/// <summary>
/// What an external service is allowed to do back.
/// </summary>
/// <remarks>
/// The connectors configured no primary handler and no response limit, so any service one called
/// could redirect the request — carrying that connector's credentials — and answer with an unbounded
/// body. The workflow engine had been hardened against exactly this one cycle earlier; the two
/// outbound paths were never one rule, which is why fixing one did not fix the other.
/// </remarks>
public class OutboundPolicyTests
{
    [Fact]
    public void TheSharedHandlerDoesNotFollowRedirects()
        => Assert.False(OutboundHttpPolicy.CreateHandler().AllowAutoRedirect);

    [Fact]
    public void TheSharedPolicyCapsTheResponse()
    {
        using var client = new HttpClient();
        OutboundHttpPolicy.Configure(client);

        Assert.Equal(OutboundHttpPolicy.MaxResponseBytes, client.MaxResponseContentBufferSize);
    }

    [Theory]
    [InlineData("Check\rStock")]
    [InlineData("Check\nStock")]
    [InlineData("Check\"Stock")]
    public void AHeaderValueThatWouldBreakTheHeaderIsRefused(string value)
        => Assert.Throws<ArgumentException>(() => OutboundHttpPolicy.RequireHeaderSafe(value, "The SOAP action"));

    [Fact]
    public void AnOrdinaryHeaderValuePasses()
        => Assert.Equal("CheckStock", OutboundHttpPolicy.RequireHeaderSafe("CheckStock", "The SOAP action"));

    [Fact]
    public async Task ARestConnectorRefusesAnAbsoluteEndpoint()
    {
        var options = new ConnectorOptions { Name = "acme", BaseUrl = "https://acme.example.com" };
        using var http = new HttpClient { BaseAddress = new Uri(options.BaseUrl) };
        var connector = new RestConnector(http, options, NullLogger<RestConnector>.Instance);

        var error = await Assert.ThrowsAsync<ArgumentException>(
            () => connector.ExecuteAsync<object, object>("https://elsewhere.example.com/steal", new { }));

        Assert.Contains("absolute URI", error.Message);
    }

    [Fact]
    public void EveryConnectorClientCarriesThePolicy()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFoundryRestConnector("rest", o => o.BaseUrl = "https://a.example.com");
        services.AddFoundrySoapConnector("soap", o => o.BaseUrl = "https://b.example.com");
        services.AddFoundryGraphQLConnector("gql", o => o.BaseUrl = "https://c.example.com");

        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();

        foreach (var name in new[] { "rest", "soap", "gql" })
        {
            using var client = factory.CreateClient(name);
            Assert.Equal(OutboundHttpPolicy.MaxResponseBytes, client.MaxResponseContentBufferSize);
        }
    }
}
