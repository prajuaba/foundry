using System;
using System.Threading;
using System.Threading.Tasks;
using Confluent.Kafka;
using Foundry.Kafka.Configuration;
using Microsoft.Extensions.Options;

namespace Foundry.Kafka.Producer;

/// <summary>
/// Implementation of <see cref="IKafkaProducer"/> using Confluent.Kafka.
/// </summary>
public class KafkaProducer : IKafkaProducer
{
    private readonly IProducer<string, string> _producer;
    private bool _disposed = false;

    /// <summary>
    /// Public constructor for unit testing.
    /// </summary>
    public KafkaProducer(IProducer<string, string> producer)
    {
        _producer = producer;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="KafkaProducer"/> class.
    /// </summary>
    /// <param name="options">The Kafka options.</param>
    public KafkaProducer(IOptions<KafkaOptions> options)
    {
        var kafkaOptions = options.Value;

        if (string.IsNullOrWhiteSpace(kafkaOptions.BootstrapServers))
        {
            // Building a producer with no brokers fails later, inside librdkafka, at the first
            // publish rather than at startup -- so a misconfigured application starts cleanly and
            // then silently accumulates unpublished outbox rows.
            throw new InvalidOperationException(
                "Kafka BootstrapServers is not configured. Set it via AddFoundryKafka(options => "
                + "options.BootstrapServers = ...) or the bound configuration section.");
        }

        var producerConfig = new ProducerConfig
        {
            BootstrapServers = kafkaOptions.BootstrapServers,
            ClientId = kafkaOptions.ClientId,
            LingerMs = kafkaOptions.ProducerOptions.LingerMs,
            Acks = ParseAcks(kafkaOptions.ProducerOptions.Acks),
            CompressionType = Enum.TryParse<CompressionType>(kafkaOptions.ProducerOptions.CompressionType, true, out var compType) ? compType : Confluent.Kafka.CompressionType.None
        };

        _producer = new ProducerBuilder<string, string>(producerConfig).Build();
    }

    /// <summary>
    /// Converts a configured acknowledgement level into a valid <see cref="Acks"/> value.
    /// </summary>
    /// <remarks>
    /// The configured integer used to be cast straight to the enum. <c>Acks.All</c> is <c>-1</c>, so
    /// a plausible-looking value such as <c>2</c> or <c>3</c> produced an undefined enum member that
    /// librdkafka then interpreted however it liked — a silently wrong durability setting on the
    /// path the outbox depends on.
    /// </remarks>
    internal static Acks ParseAcks(int configured)
    {
        if (!Enum.IsDefined(typeof(Acks), (Acks)configured))
        {
            throw new InvalidOperationException(
                $"Kafka Acks value {configured} is not valid. Use 0 (none), 1 (leader) or -1 (all replicas).");
        }

        return (Acks)configured;
    }

    /// <inheritdoc/>
    public Task<Confluent.Kafka.DeliveryResult<string, string>> ProduceAsync(string topic, string key, string value, Confluent.Kafka.Headers? headers = null, CancellationToken ct = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(KafkaProducer));

        return _producer.ProduceAsync(topic, new Message<string, string> { Key = key, Value = value, Headers = headers }, ct);
    }

    /// <inheritdoc/>
    public void Produce(string topic, string key, string value, Action<Confluent.Kafka.DeliveryReport<string, string>>? deliveryHandler = null)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(KafkaProducer));

        _producer.Produce(topic, new Message<string, string> { Key = key, Value = value }, deliveryHandler);
    }

    /// <inheritdoc/>
    public void Flush(TimeSpan timeout)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(KafkaProducer));

        _producer.Flush(timeout);
    }

    /// <summary>
    /// Disposes the producer.
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Disposes the producer resources.
    /// </summary>
    /// <param name="disposing">Whether to dispose managed resources.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            _producer?.Dispose();
            _disposed = true;
        }
    }
}
