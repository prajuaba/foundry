using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;

namespace Foundry.Connectors;

public static class FoundryConnectorServiceCollectionExtensions
{
    public static IHttpClientBuilder AddFoundryRestConnector(this IServiceCollection services, string name, Action<ConnectorOptions> configureOptions)
    {
        var options = new ConnectorOptions { Name = name, Type = ConnectorType.REST };
        configureOptions(options);

        services.AddSingleton(options);

        var builder = services.AddHttpClient<RestConnector>(name, client =>
        {
            if (!string.IsNullOrEmpty(options.BaseUrl))
            {
                client.BaseAddress = new Uri(options.BaseUrl);
            }
            client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        });

        builder.AddStandardResilienceHandler();

        services.AddSingleton<IFoundryConnector>(sp => sp.GetRequiredService<RestConnector>());
        return builder;
    }

    public static IHttpClientBuilder AddFoundrySoapConnector(this IServiceCollection services, string name, Action<ConnectorOptions> configureOptions)
    {
        var options = new ConnectorOptions { Name = name, Type = ConnectorType.SOAP };
        configureOptions(options);

        services.AddSingleton(options);

        var builder = services.AddHttpClient<SoapConnector>(name, client =>
        {
            if (!string.IsNullOrEmpty(options.BaseUrl))
            {
                client.BaseAddress = new Uri(options.BaseUrl);
            }
            client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        });

        builder.AddStandardResilienceHandler();

        services.AddSingleton<IFoundryConnector>(sp => sp.GetRequiredService<SoapConnector>());
        return builder;
    }

    public static IHttpClientBuilder AddFoundryGraphQLConnector(this IServiceCollection services, string name, Action<ConnectorOptions> configureOptions)
    {
        var options = new ConnectorOptions { Name = name, Type = ConnectorType.GraphQL };
        configureOptions(options);

        services.AddSingleton(options);

        var builder = services.AddHttpClient<GraphQLConnector>(name, client =>
        {
            if (!string.IsNullOrEmpty(options.BaseUrl))
            {
                client.BaseAddress = new Uri(options.BaseUrl);
            }
            client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        });

        builder.AddStandardResilienceHandler();

        services.AddSingleton<IFoundryConnector>(sp => sp.GetRequiredService<GraphQLConnector>());
        return builder;
    }
}
