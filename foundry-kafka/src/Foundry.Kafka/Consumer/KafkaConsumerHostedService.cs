using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Confluent.Kafka;
using Foundry.Kafka.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;

namespace Foundry.Kafka.Consumer;

/// <summary>
/// A hosted service that consumes messages from Kafka and processes them.
/// </summary>
public class KafkaConsumerHostedService : BackgroundService
{
    private readonly IConsumer<string, string> _consumer;
    private readonly ILogger<KafkaConsumerHostedService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly ConsumerOptions _options;

    /// <summary>
    /// Public constructor for unit testing.
    /// </summary>
    public KafkaConsumerHostedService(
        IConsumer<string, string> consumer,
        IOptions<KafkaOptions> options,
        ILogger<KafkaConsumerHostedService> logger,
        IServiceProvider serviceProvider)
    {
        _consumer = consumer;
        _options = options.Value.ConsumerOptions;
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="KafkaConsumerHostedService"/> class.
    /// </summary>
    /// <param name="options">The Kafka options.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="serviceProvider">The service provider for creating scopes.</param>
    public KafkaConsumerHostedService(
        IOptions<KafkaOptions> options,
        ILogger<KafkaConsumerHostedService> logger,
        IServiceProvider serviceProvider)
    {
        _options = options.Value.ConsumerOptions;
        _logger = logger;
        _serviceProvider = serviceProvider;

        var consumerConfig = new ConsumerConfig
        {
            BootstrapServers = options.Value.BootstrapServers,
            GroupId = _options.GroupId,
            AutoOffsetReset = Enum.TryParse<AutoOffsetReset>(_options.AutoOffsetReset, true, out var resetMode) ? resetMode : Confluent.Kafka.AutoOffsetReset.Latest,
            EnableAutoCommit = _options.EnableAutoCommit,
            SessionTimeoutMs = _options.SessionTimeoutMs
        };

        _consumer = new ConsumerBuilder<string, string>(consumerConfig).Build();
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrEmpty(_options.GroupId))
        {
            throw new InvalidOperationException("Consumer GroupId must be configured.");
        }

        var topics = _options.TopicApiMappings.Keys;
        _logger.LogInformation("Subscribing to topics: {Topics}", string.Join(", ", topics));
        _consumer.Subscribe(topics);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var consumeResult = _consumer.Consume(stoppingToken);
                if (consumeResult == null) continue;

                await ProcessMessageAsync(consumeResult, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Expected when cancellation is requested
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error consuming message from Kafka");
                // Optionally implement retry logic or delay here
                await Task.Delay(1000, stoppingToken); // Delay before continuing
            }
        }

        _consumer.Unsubscribe();
        _consumer.Close();
    }

    private async Task ProcessMessageAsync(ConsumeResult<string, string> consumeResult, CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<IKafkaMessageHandler>();

        var headers = new Dictionary<string, string>();
        foreach (var header in consumeResult.Message.Headers)
        {
            headers[header.Key] = header.GetValueBytes() is byte[] bytes ? System.Text.Encoding.UTF8.GetString(bytes) : string.Empty;
        }

        try
        {
            await handler.HandleAsync(consumeResult.Topic, consumeResult.Message.Key, consumeResult.Message.Value, headers, ct);
            _consumer.Commit(consumeResult); // Commit offset on successful handling
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling message from topic {Topic}", consumeResult.Topic);
            // Do not commit offset to retry the message
            throw; // Re-throw to prevent committing offset
        }
    }

    /// <inheritdoc/>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _consumer?.Unsubscribe();
        await base.StopAsync(cancellationToken);
    }
}
