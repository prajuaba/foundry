using Foundry.Connectors;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Foundry.Connectors.Tests;

/// <summary>
/// Registering more than one connector.
/// </summary>
/// <remarks>
/// A single connector is the demo case; several is the real one — an application integrates a CRM
/// and a billing provider, not one anonymous external service. Every connector carries its own base
/// URL and its own credentials, so if registrations interfere the failure mode is sending one
/// service's API key to another service's endpoint. That request succeeds or fails on the remote
/// side, so nothing local reports a problem.
/// </remarks>
public class ConnectorRegistrationTests
{
    private static ServiceProvider Build(Action<IServiceCollection> configure)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        configure(services);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void OneConnector_IsResolvable()
    {
        using var provider = Build(services => services.AddFoundryRestConnector("crm", options =>
        {
            options.BaseUrl = "https://crm.example.com/";
            options.AuthType = AuthenticationType.ApiKey;
            options.ApiKey = "crm-key";
        }));

        var connector = Assert.Single(provider.GetServices<IFoundryConnector>());
        Assert.Equal("crm", connector.Name);
        Assert.Equal(ConnectorType.REST, connector.Type);
    }

    [Fact]
    public void TwoConnectorsOfTheSameType_AreBothRegistered()
    {
        using var provider = Build(services =>
        {
            services.AddFoundryRestConnector("crm", o => o.BaseUrl = "https://crm.example.com/");
            services.AddFoundryRestConnector("billing", o => o.BaseUrl = "https://billing.example.com/");
        });

        var names = provider.GetServices<IFoundryConnector>().Select(c => c.Name).OrderBy(n => n).ToList();

        Assert.Equal(["billing", "crm"], names);
    }

    [Fact]
    public void EachConnectorKeepsItsOwnName()
    {
        // ConnectorOptions was registered by type rather than per connector, so the last
        // registration won and every connector reported the same identity.
        using var provider = Build(services =>
        {
            services.AddFoundryRestConnector("crm", o => o.BaseUrl = "https://crm.example.com/");
            services.AddFoundryRestConnector("billing", o => o.BaseUrl = "https://billing.example.com/");
        });

        var connectors = provider.GetServices<IFoundryConnector>().ToList();

        Assert.Equal(2, connectors.Select(c => c.Name).Distinct().Count());
    }

    [Fact]
    public void EachConnectorTargetsItsOwnBaseUrl()
    {
        // The consequence that matters: a shared options instance meant both connectors pointed at
        // one base URL, so calls intended for billing went to the CRM host carrying billing's
        // credentials.
        using var provider = Build(services =>
        {
            services.AddFoundryRestConnector("crm", o => o.BaseUrl = "https://crm.example.com/");
            services.AddFoundryRestConnector("billing", o => o.BaseUrl = "https://billing.example.com/");
        });

        var baseUrls = provider.GetServices<IFoundryConnector>()
            .OfType<RestConnector>()
            .Select(c => c.BaseAddress?.ToString())
            .OrderBy(u => u)
            .ToList();

        Assert.Equal(["https://billing.example.com/", "https://crm.example.com/"], baseUrls);
    }

    [Fact]
    public void ConnectorsOfDifferentTypes_Coexist()
    {
        using var provider = Build(services =>
        {
            services.AddFoundryRestConnector("crm", o => o.BaseUrl = "https://crm.example.com/");
            services.AddFoundrySoapConnector("legacy", o => o.BaseUrl = "https://legacy.example.com/");
            services.AddFoundryGraphQLConnector("catalog", o => o.BaseUrl = "https://catalog.example.com/");
        });

        var byType = provider.GetServices<IFoundryConnector>().ToDictionary(c => c.Name, c => c.Type);

        Assert.Equal(ConnectorType.REST, byType["crm"]);
        Assert.Equal(ConnectorType.SOAP, byType["legacy"]);
        Assert.Equal(ConnectorType.GraphQL, byType["catalog"]);
    }

    [Fact]
    public void CredentialsDoNotLeakBetweenConnectors()
    {
        // The security-relevant assertion. Two connectors with different API keys must not end up
        // sharing one HttpClient's default headers.
        using var provider = Build(services =>
        {
            services.AddFoundryRestConnector("crm", o =>
            {
                o.BaseUrl = "https://crm.example.com/";
                o.AuthType = AuthenticationType.ApiKey;
                o.ApiKey = "crm-key";
            });
            services.AddFoundryRestConnector("billing", o =>
            {
                o.BaseUrl = "https://billing.example.com/";
                o.AuthType = AuthenticationType.ApiKey;
                o.ApiKey = "billing-key";
            });
        });

        var keysByName = provider.GetServices<IFoundryConnector>()
            .OfType<RestConnector>()
            .ToDictionary(c => c.Name, c => c.ConfiguredApiKeyValues().ToList());

        Assert.Equal(["crm-key"], keysByName["crm"]);
        Assert.Equal(["billing-key"], keysByName["billing"]);
    }
}
