using System;
using Foundry.Kafka.Configuration;
using Foundry.Kafka.Consumer;
using Foundry.Kafka.Producer;
using Foundry.Kafka.Bridge;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Foundry.Kafka.DependencyInjection;

/// <summary>
/// Extension methods for configuring Kafka services.
/// </summary>
public static class KafkaServiceCollectionExtensions
{
    /// <summary>
    /// Adds Kafka producer services to the service collection.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="section">The configuration section containing Kafka settings.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddFoundryKafkaProducer(this IServiceCollection services, IConfiguration section)
    {
        if (section == null)
            throw new ArgumentNullException(nameof(section));

        services.Configure<KafkaOptions>(section);
        services.TryAddSingleton<IKafkaProducer, KafkaProducer>();
        services.TryAddSingleton<Foundry.Core.Outbox.IOutboxDispatcher, KafkaOutboxDispatcher>();
        services.TryAddTransient<Diagnostics.KafkaHealthCheck>();
        return services;
    }

    /// <summary>
    /// Adds Kafka consumer bridge services to the service collection.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="section">The configuration section containing Kafka settings.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddFoundryKafkaConsumerBridge(this IServiceCollection services, IConfiguration section)
    {
        if (section == null)
            throw new ArgumentNullException(nameof(section));

        services.Configure<KafkaOptions>(section);
        
        // Ensure IKafkaProducer is registered for routing failed messages to the DLQ
        services.TryAddSingleton<IKafkaProducer, KafkaProducer>();
        
        services.AddSingleton<IKafkaMessageHandler, KafkaToApiBridgeHandler>();
        services.AddHostedService<KafkaConsumerHostedService>();

        // Register named HttpClient with default policies
        services.AddHttpClient("KafkaBridge", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        return services;
    }
}
