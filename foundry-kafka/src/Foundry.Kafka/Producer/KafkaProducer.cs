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
        var producerConfig = new ProducerConfig
        {
            BootstrapServers = kafkaOptions.BootstrapServers,
            ClientId = kafkaOptions.ClientId,
            LingerMs = kafkaOptions.ProducerOptions.LingerMs,
            Acks = (Acks)kafkaOptions.ProducerOptions.Acks,
            CompressionType = Enum.TryParse<CompressionType>(kafkaOptions.ProducerOptions.CompressionType, true, out var compType) ? compType : Confluent.Kafka.CompressionType.None
        };

        _producer = new ProducerBuilder<string, string>(producerConfig).Build();
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
