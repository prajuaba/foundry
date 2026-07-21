using System;
using System.Threading;
using System.Threading.Tasks;

namespace Foundry.Kafka.Producer;

/// <summary>
/// Defines the interface for a Kafka producer.
/// </summary>
public interface IKafkaProducer : IDisposable
{
    /// <summary>
    /// Asynchronously produces a message to a Kafka topic.
    /// </summary>
    /// <param name="topic">The Kafka topic to produce to.</param>
    /// <param name="key">The key for the message.</param>
    /// <param name="value">The value for the message.</param>
    /// <param name="headers">Optional Kafka message headers.</param>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>A task that represents the asynchronous operation and returns the delivery result.</returns>
    Task<Confluent.Kafka.DeliveryResult<string, string>> ProduceAsync(string topic, string key, string value, Confluent.Kafka.Headers? headers = null, CancellationToken ct = default);

    /// <summary>
    /// Synchronously produces a message to a Kafka topic.
    /// </summary>
    /// <param name="topic">The Kafka topic to produce to.</param>
    /// <param name="key">The key for the message.</param>
    /// <param name="value">The value for the message.</param>
    /// <param name="deliveryHandler">Optional delivery handler callback.</param>
    void Produce(string topic, string key, string value, Action<Confluent.Kafka.DeliveryReport<string, string>>? deliveryHandler = null);

    /// <summary>
    /// Flushes any buffered messages.
    /// </summary>
    /// <param name="timeout">The timeout for the flush operation.</param>
    void Flush(TimeSpan timeout);
}
