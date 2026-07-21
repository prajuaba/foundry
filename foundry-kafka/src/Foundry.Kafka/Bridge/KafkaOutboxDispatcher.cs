using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundry.Core.Outbox;
using Foundry.Kafka.Producer;

namespace Foundry.Kafka.Bridge;

/// <summary>
/// A concrete implementation of <see cref="IOutboxDispatcher"/> that publishes enqueued outbox messages
/// to corresponding Kafka topics using a resilient producer.
/// </summary>
public class KafkaOutboxDispatcher : IOutboxDispatcher
{
    private readonly IKafkaProducer _producer;

    /// <summary>
    /// Initializes a new instance of the <see cref="KafkaOutboxDispatcher"/> class.
    /// </summary>
    /// <param name="producer">The resilient Kafka producer.</param>
    public KafkaOutboxDispatcher(IKafkaProducer producer)
    {
        _producer = producer ?? throw new ArgumentNullException(nameof(producer));
    }

    /// <inheritdoc />
    public async Task DispatchAsync(string eventType, string payload, string? correlationId = null, string? traceParent = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(eventType)) throw new ArgumentNullException(nameof(eventType));

        // 1. Resolve a clean kebab-case topic name from the event type (e.g. "OrderEvent" -> "order-event-events")
        var topic = ResolveTopicName(eventType);

        // 2. Use a unique key for partition distribution
        var key = Guid.NewGuid().ToString();

        // 3. Inject trace correlation context into Kafka headers
        var headers = new Confluent.Kafka.Headers();
        if (!string.IsNullOrEmpty(correlationId))
        {
            headers.Add("X-Correlation-Id", System.Text.Encoding.UTF8.GetBytes(correlationId));
        }
        if (!string.IsNullOrEmpty(traceParent))
        {
            headers.Add("traceparent", System.Text.Encoding.UTF8.GetBytes(traceParent));
        }

        // 4. Resiliently produce to Kafka topic with headers
        await _producer.ProduceAsync(topic, key, payload, headers, ct);
    }

    private static string ResolveTopicName(string eventType)
    {
        // Extract type name from qualified assembly name
        var cleanType = eventType.Split(',')[0];
        var parts = cleanType.Split('.');
        var name = parts.Last();

        return $"{CamelToKebab(name)}-events";
    }

    private static string CamelToKebab(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        return System.Text.RegularExpressions.Regex.Replace(
            input,
            "([a-z0-9])([A-Z])",
            "$1-$2"
        ).ToLowerInvariant();
    }
}
