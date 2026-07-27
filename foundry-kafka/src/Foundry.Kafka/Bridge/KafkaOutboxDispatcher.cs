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

        // Checked here rather than left to the broker. An illegal name is refused at produce time
        // with librdkafka's "Broker: Invalid topic", which names neither the topic nor the event
        // type that produced it -- and the outbox worker retries that failure indefinitely while
        // reporting nothing, so the first symptom is a queue that never drains.
        if (!topic.All(IsLegalTopicCharacter))
        {
            var illegal = new string(topic.Where(c => !IsLegalTopicCharacter(c)).Distinct().ToArray());

            throw new ArgumentException(
                $"Event type '{eventType}' yields the topic '{topic}', which Kafka will reject: "
                + $"topic names may contain only letters, digits, '.', '_' and '-' (found: {illegal}).",
                nameof(eventType));
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
        => $"{CamelToKebab(ExtractTypeName(eventType))}{TopicSuffix}";

    /// <summary>
    /// Reduces a CLR type name — possibly assembly-qualified, generic or nested — to the simple name
    /// the topic is derived from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This split the assembly-qualified name on a comma and took the last dotted segment, which
    /// leaves the nested-type separator in place: a nested event type such as
    /// <c>Orders+Placed</c> produced the topic <c>orders+placed-events</c>, and <c>+</c> is not a
    /// legal Kafka topic character. The publish failed with librdkafka's <c>Broker: Invalid topic</c>,
    /// the outbox worker logged it and retried, and the message never left — forever, quietly.
    /// A nested record is an ordinary way to declare an event.
    /// </para>
    /// <para>
    /// A generic event is named for what it is about: <c>EntityMutationEvent&lt;Order&gt;</c>
    /// publishes to <c>order-events</c>, not <c>entity-mutation-events</c>, so each entity keeps its
    /// own topic. That was previously an accident of where the comma fell in the qualified name; it
    /// is now the stated rule.
    /// </para>
    /// </remarks>
    internal static string ExtractTypeName(string eventType)
    {
        // A generic wraps the thing it is about, so the first type argument wins when there is one.
        var genericStart = eventType.IndexOf("[[", StringComparison.Ordinal);
        var source = genericStart >= 0 ? eventType.Substring(genericStart + 2) : eventType;

        // Drop the assembly qualification, then any remaining brackets and the arity marker.
        source = source.Split(',')[0].Split('[')[0].Split('`')[0];

        // '+' separates a nested type from its declaring type, exactly as '.' separates namespaces.
        var segments = source.Split('.', '+');
        return segments[segments.Length - 1].Trim();
    }

    /// <summary>Characters Kafka permits in a topic name.</summary>
    private static bool IsLegalTopicCharacter(char c)
        => char.IsAsciiLetterOrDigit(c) || c == '.' || c == '_' || c == '-';

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
