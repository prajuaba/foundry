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
        if (string.IsNullOrWhiteSpace(eventType))
        {
            throw new ArgumentException("Event type must be provided.", nameof(eventType));
        }

        // 1. Resolve a clean kebab-case topic name from the event type (e.g. "OrderEvent" -> "order-event-events")
        var topic = ResolveTopicName(eventType);

        // An event type such as "Foo." leaves an empty final segment, which used to produce the
        // topic "-events" and publish there successfully. A message on a garbage topic is as lost
        // as one never sent, so this fails instead of succeeding quietly.
        if (topic == TopicSuffix)
        {
            throw new ArgumentException(
                $"Event type '{eventType}' does not yield a usable topic name.", nameof(eventType));
        }

        // 2. Partition key.
        //
        // Kafka guarantees ordering only within a partition, and the partition is selected by key.
        // This used to be a fresh Guid per message, which spread one entity's mutations across
        // partitions and allowed a consumer to apply Update before the Insert it depends on --
        // defeating the purpose of an ordered outbox, with every publish still reporting success.
        var key = ResolvePartitionKey(eventType, payload);

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

    private const string TopicSuffix = "-events";

    private static string ResolveTopicName(string eventType)
    {
        // Extract type name from qualified assembly name
        var cleanType = eventType.Split(',')[0];
        var parts = cleanType.Split('.');
        var name = parts.Last();

        return $"{CamelToKebab(name)}{TopicSuffix}";
    }

    /// <summary>
    /// Chooses the Kafka partition key for a message, preferring the mutated entity's id.
    /// </summary>
    /// <remarks>
    /// Same entity to same key to same partition, which is what preserves the order of an entity's
    /// mutations. Falls back to the event type when no id can be read: that concentrates a type's
    /// events on one partition, which costs some parallelism but keeps ordering intact. Reading the
    /// key must never prevent publication, so any parse failure falls back rather than throwing --
    /// a keying problem should not become message loss.
    /// </remarks>
    private static string ResolvePartitionKey(string eventType, string payload)
    {
        if (!string.IsNullOrWhiteSpace(payload))
        {
            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(payload);
                var root = document.RootElement;

                if (root.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    // EntityMutationEvent<T> nests the entity, so Entity.Id is the usual location.
                    if (TryFindProperty(root, "entity", out var entity)
                        && entity.ValueKind == System.Text.Json.JsonValueKind.Object
                        && TryFindProperty(entity, "id", out var nestedId))
                    {
                        var key = ScalarToString(nestedId);
                        if (!string.IsNullOrWhiteSpace(key)) return key!;
                    }

                    // A flatter payload may carry the id directly.
                    foreach (var candidate in new[] { "entityId", "id" })
                    {
                        if (TryFindProperty(root, candidate, out var directId))
                        {
                            var key = ScalarToString(directId);
                            if (!string.IsNullOrWhiteSpace(key)) return key!;
                        }
                    }
                }
            }
            catch (System.Text.Json.JsonException)
            {
                // Opaque or non-JSON payload; fall through to the event type.
            }
        }

        return eventType;
    }

    private static bool TryFindProperty(
        System.Text.Json.JsonElement element,
        string name,
        out System.Text.Json.JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? ScalarToString(System.Text.Json.JsonElement element) => element.ValueKind switch
    {
        System.Text.Json.JsonValueKind.String => element.GetString(),
        System.Text.Json.JsonValueKind.Number => element.GetRawText(),
        _ => null
    };

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
