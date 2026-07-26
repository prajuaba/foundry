using System;
using Foundry.Kafka.Configuration;
using Foundry.Kafka.Consumer;
using Foundry.Kafka.Producer;
using Foundry.Kafka.Bridge;
using Foundry.Kafka.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Extension methods for configuring Kafka services.
/// </summary>
public static class KafkaServiceCollectionExtensions
{
    /// <summary>
    /// Adds Foundry Kafka producer, consumer hosted services, and outbox dispatcher.
    /// </summary>
    public static IServiceCollection AddFoundryKafka(this IServiceCollection services, Action<KafkaOptions>? configureOptions = null)
    {
        if (configureOptions != null)
        {
            services.Configure(configureOptions);
        }

        services.TryAddSingleton<IKafkaProducer, KafkaProducer>();
        services.TryAddSingleton<Foundry.Core.Outbox.IOutboxDispatcher, KafkaOutboxDispatcher>();
        services.TryAddTransient<KafkaHealthCheck>();
        services.AddSingleton<IKafkaMessageHandler, KafkaToApiBridgeHandler>();
        services.AddHostedService<KafkaConsumerHostedService>();

        services.AddHttpClient("KafkaBridge", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        return services;
    }

    /// <summary>
    /// Adds Kafka producer services to the service collection.
    /// </summary>
    public static IServiceCollection AddFoundryKafkaProducer(this IServiceCollection services, IConfiguration section)
    {
        if (section == null)
            throw new ArgumentNullException(nameof(section));

        services.Configure<KafkaOptions>(section);
        services.TryAddSingleton<IKafkaProducer, KafkaProducer>();
        services.TryAddSingleton<Foundry.Core.Outbox.IOutboxDispatcher, KafkaOutboxDispatcher>();
        services.TryAddTransient<KafkaHealthCheck>();
        return services;
    }

    /// <summary>
    /// Adds Kafka consumer bridge services to the service collection.
    /// </summary>
    public static IServiceCollection AddFoundryKafkaConsumerBridge(this IServiceCollection services, IConfiguration section)
    {
        if (section == null)
            throw new ArgumentNullException(nameof(section));

        services.Configure<KafkaOptions>(section);
        
        services.TryAddSingleton<IKafkaProducer, KafkaProducer>();
        services.AddSingleton<IKafkaMessageHandler, KafkaToApiBridgeHandler>();
        services.AddHostedService<KafkaConsumerHostedService>();

        services.AddHttpClient("KafkaBridge", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        return services;
    }
}
