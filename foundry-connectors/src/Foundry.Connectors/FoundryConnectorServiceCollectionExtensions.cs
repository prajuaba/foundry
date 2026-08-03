using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;

namespace Foundry.Connectors;

/// <summary>
/// Registration helpers for external service connectors.
/// </summary>
/// <remarks>
/// <para>
/// Each connector is registered against a <em>named</em> <c>HttpClient</c> and receives its own
/// <see cref="ConnectorOptions"/> instance, captured when it is registered.
/// </para>
/// <para>
/// These methods used to call <c>services.AddSingleton(options)</c> and
/// <c>services.AddHttpClient&lt;RestConnector&gt;(name, ...)</c>. Both key on the <em>type</em>
/// rather than the connector name, so registering a second connector of the same type replaced the
/// first: <c>ConnectorOptions</c> resolved to whichever was registered last, and the typed-client
/// registration was overwritten. An application integrating a CRM and a billing provider therefore
/// ended up with both connectors pointing at one base URL and carrying one set of credentials —
/// sending one service's API key to the other service's endpoint. Nothing local reported a problem,
/// because the resulting request is perfectly well-formed.
/// </para>
/// </remarks>
public static class FoundryConnectorServiceCollectionExtensions
{
    /// <summary>Registers a REST connector under <paramref name="name"/>.</summary>
    public static IHttpClientBuilder AddFoundryRestConnector(this IServiceCollection services, string name, Action<ConnectorOptions> configureOptions)
        => AddConnector(services, name, ConnectorType.REST, configureOptions,
            (client, options, loggerFactory) =>
                new RestConnector(client, options, loggerFactory.CreateLogger<RestConnector>()));

    /// <summary>Registers a SOAP connector under <paramref name="name"/>.</summary>
    public static IHttpClientBuilder AddFoundrySoapConnector(this IServiceCollection services, string name, Action<ConnectorOptions> configureOptions)
        => AddConnector(services, name, ConnectorType.SOAP, configureOptions,
            (client, options, loggerFactory) =>
                new SoapConnector(client, options, loggerFactory.CreateLogger<SoapConnector>()));

    /// <summary>Registers a GraphQL connector under <paramref name="name"/>.</summary>
    public static IHttpClientBuilder AddFoundryGraphQLConnector(this IServiceCollection services, string name, Action<ConnectorOptions> configureOptions)
        => AddConnector(services, name, ConnectorType.GraphQL, configureOptions,
            (client, options, loggerFactory) =>
                new GraphQLConnector(client, options, loggerFactory.CreateLogger<GraphQLConnector>()));

    private static IHttpClientBuilder AddConnector(
        IServiceCollection services,
        string name,
        ConnectorType type,
        Action<ConnectorOptions> configureOptions,
        Func<System.Net.Http.HttpClient, ConnectorOptions, ILoggerFactory, IFoundryConnector> factory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configureOptions);

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A connector name is required; it keys the HttpClient and its configuration.", nameof(name));
        }

        var options = new ConnectorOptions { Name = name, Type = type };
        configureOptions(options);

        var builder = services.AddHttpClient(name, client =>
        {
            if (!string.IsNullOrEmpty(options.BaseUrl))
            {
                client.BaseAddress = new Uri(options.BaseUrl);
            }
            // The connector's own timeout, then the shared limits. A connector may be given a
            // shorter timeout than the framework default; it may not opt out of the response cap.
            Foundry.Core.Http.OutboundHttpPolicy.Configure(client);
            client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        });

        // The same policy the workflow engine's external actions use. Both call systems Foundry does
        // not control, and until now only one of them said what an external service is allowed to do
        // back -- redirect this request somewhere else, or answer with an unbounded body.
        builder.ConfigurePrimaryHttpMessageHandler(Foundry.Core.Http.OutboundHttpPolicy.CreateHandler);

        builder.AddStandardResilienceHandler();

        // The options instance is captured here rather than resolved from the container, so each
        // connector keeps its own configuration no matter how many are registered.
        services.AddSingleton<IFoundryConnector>(sp => factory(
            sp.GetRequiredService<System.Net.Http.IHttpClientFactory>().CreateClient(name),
            options,
            sp.GetRequiredService<ILoggerFactory>()));

        return builder;
    }
}
