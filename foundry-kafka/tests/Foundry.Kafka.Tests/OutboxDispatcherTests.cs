using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Foundry.Kafka.Bridge;
using Foundry.Kafka.Producer;
using Xunit;

namespace Foundry.Kafka.Tests;

/// <summary>
/// Topic naming, partition keying and header propagation in <see cref="KafkaOutboxDispatcher"/>.
/// </summary>
/// <remarks>
/// The transactional outbox promises that domain mutations reach consumers, in order, at least once.
/// Everything asserted here is part of that promise, and none of it fails loudly when broken: a
/// mis-keyed message is delivered successfully to the wrong partition, and a consumer then applies
/// an update before the insert it depends on.
/// </remarks>
public class OutboxDispatcherTests
{
    /// <summary>Captures what would have been produced, instead of talking to a broker.</summary>
    private sealed class RecordingProducer : IKafkaProducer
    {
        public List<(string Topic, string Key, string Value, Headers? Headers)> Produced { get; } = [];

        public Task<DeliveryResult<string, string>> ProduceAsync(
            string topic, string key, string value, Headers? headers = null, CancellationToken ct = default)
        {
            Produced.Add((topic, key, value, headers));
            return Task.FromResult(new DeliveryResult<string, string>
            {
                Topic = topic,
                Message = new Message<string, string> { Key = key, Value = value, Headers = headers }
            });
        }

        public void Produce(string topic, string key, string value,
            Action<DeliveryReport<string, string>>? deliveryHandler = null)
            => Produced.Add((topic, key, value, null));

        public void Flush(TimeSpan timeout) { }

        public void Dispose() { }
    }

    private static string? HeaderValue(Headers? headers, string name)
    {
        if (headers is null) return null;
        return headers.TryGetLastBytes(name, out var bytes) ? Encoding.UTF8.GetString(bytes) : null;
    }

    // ---- topic naming ----

    [Theory]
    [InlineData("OrderEvent", "order-event-events")]
    [InlineData("Order", "order-events")]
    [InlineData("CustomerAddressChanged", "customer-address-changed-events")]
    [InlineData("MyApp.Domain.OrderEvent", "order-event-events")]
    [InlineData("MyApp.Domain.OrderEvent, MyApp, Version=1.0.0.0", "order-event-events")]
    // A nested event type. '+' separates it from its declaring type and is not a legal Kafka topic
    // character, so this used to produce "orders+placed-events" and fail at the broker with
    // "Broker: Invalid topic" -- which the outbox worker logged and retried forever.
    [InlineData("MyApp.Domain.Orders+Placed", "placed-events")]
    [InlineData("MyApp.Domain.Orders+Placed, MyApp", "placed-events")]
    // A generic event is named for what it is about, so each entity keeps its own topic rather than
    // everything landing on entity-mutation-events. Previously an accident of where the comma fell.
    [InlineData("Foundry.Core.Outbox.EntityMutationEvent`1[[MyApp.Domain.Order, MyApp]], Foundry.Core", "order-events")]
    [InlineData("Foundry.Core.Outbox.EntityMutationEvent`1[[MyApp.Domain.Orders+Placed, MyApp]], Foundry.Core", "placed-events")]
    public async Task TopicName_IsDerivedFromTheEventTypeName(string eventType, string expectedTopic)
    {
        var producer = new RecordingProducer();
        await new KafkaOutboxDispatcher(producer).DispatchAsync(eventType, "{}");

        Assert.Equal(expectedTopic, producer.Produced[0].Topic);
    }

    [Fact]
    public async Task AnEventTypeYieldingAnIllegalTopicName_IsRejectedBeforePublishing()
    {
        // Refused here rather than at the broker, whose error names neither the topic nor the event
        // type that produced it.
        var producer = new RecordingProducer();
        var dispatcher = new KafkaOutboxDispatcher(producer);

        var error = await Assert.ThrowsAnyAsync<ArgumentException>(
            () => dispatcher.DispatchAsync("My Event!", "{}"));

        Assert.Contains("My Event!", error.Message);
        Assert.Empty(producer.Produced);
    }

    [Fact]
    public async Task EmptyEventType_IsRejected()
    {
        var dispatcher = new KafkaOutboxDispatcher(new RecordingProducer());

        await Assert.ThrowsAnyAsync<ArgumentException>(() => dispatcher.DispatchAsync("", "{}"));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => dispatcher.DispatchAsync("   ", "{}"));
    }

    [Fact]
    public async Task AnEventTypeThatYieldsNoTopicName_IsRejected()
    {
        // "Foo." splits to an empty final segment, which produced the topic "-events" and published
        // there successfully. A message on a garbage topic is lost as surely as one never sent, but
        // nothing reports a failure.
        var dispatcher = new KafkaOutboxDispatcher(new RecordingProducer());

        await Assert.ThrowsAnyAsync<ArgumentException>(() => dispatcher.DispatchAsync("Foo.", "{}"));
    }

    // ---- partition keying: ordering ----

    [Fact]
    public async Task TwoEventsForTheSameEntity_ShareAPartitionKey()
    {
        // Kafka guarantees order only within a partition, and the partition is chosen by key. A
        // random key per message spreads an entity's mutations across partitions, so a consumer can
        // legitimately apply Update before Insert. The outbox exists to prevent exactly that.
        var producer = new RecordingProducer();
        var dispatcher = new KafkaOutboxDispatcher(producer);

        var insert = JsonSerializer.Serialize(new
        {
            MutationType = "Insert",
            Entity = new { Id = "6a65ba09986eed749ed7e968", Name = "Ada" }
        });
        var update = JsonSerializer.Serialize(new
        {
            MutationType = "Update",
            Entity = new { Id = "6a65ba09986eed749ed7e968", Name = "Grace" }
        });

        await dispatcher.DispatchAsync("OrderEvent", insert);
        await dispatcher.DispatchAsync("OrderEvent", update);

        Assert.Equal(producer.Produced[0].Key, producer.Produced[1].Key);
    }

    [Fact]
    public async Task DifferentEntities_GetDifferentPartitionKeys()
    {
        // Ordering must not come at the cost of funnelling every message through one partition.
        var producer = new RecordingProducer();
        var dispatcher = new KafkaOutboxDispatcher(producer);

        await dispatcher.DispatchAsync("OrderEvent",
            JsonSerializer.Serialize(new { Entity = new { Id = "aaaaaaaaaaaaaaaaaaaaaaaa" } }));
        await dispatcher.DispatchAsync("OrderEvent",
            JsonSerializer.Serialize(new { Entity = new { Id = "bbbbbbbbbbbbbbbbbbbbbbbb" } }));

        Assert.NotEqual(producer.Produced[0].Key, producer.Produced[1].Key);
    }

    [Fact]
    public async Task APayloadWithNoEntityId_StillGetsAStableKey()
    {
        // Falls back to the event type rather than a random value: less partition spread, but the
        // per-type ordering guarantee survives, which is the property that matters.
        var producer = new RecordingProducer();
        var dispatcher = new KafkaOutboxDispatcher(producer);

        await dispatcher.DispatchAsync("OrderEvent", """{"MutationType":"Insert"}""");
        await dispatcher.DispatchAsync("OrderEvent", """{"MutationType":"Update"}""");

        Assert.Equal(producer.Produced[0].Key, producer.Produced[1].Key);
        Assert.False(string.IsNullOrEmpty(producer.Produced[0].Key));
    }

    [Fact]
    public async Task AMalformedPayload_DoesNotPreventDispatch()
    {
        // The payload is opaque to the dispatcher. Failing to read a key out of it must not stop the
        // message being published -- that would turn a keying concern into message loss.
        var producer = new RecordingProducer();

        await new KafkaOutboxDispatcher(producer).DispatchAsync("OrderEvent", "this is not json");

        Assert.Single(producer.Produced);
        Assert.False(string.IsNullOrEmpty(producer.Produced[0].Key));
    }

    // ---- payload and headers ----

    [Fact]
    public async Task ThePayloadIsForwardedByteForByte()
    {
        var producer = new RecordingProducer();
        var payload = """{"MutationType":"Insert","Entity":{"Id":"abc"}}""";

        await new KafkaOutboxDispatcher(producer).DispatchAsync("OrderEvent", payload);

        Assert.Equal(payload, producer.Produced[0].Value);
    }

    [Fact]
    public async Task CorrelationAndTraceHeaders_ArePropagated()
    {
        // These are the only link between an HTTP request and the asynchronous work it triggered.
        var producer = new RecordingProducer();

        await new KafkaOutboxDispatcher(producer)
            .DispatchAsync("OrderEvent", "{}", "correlation-123", "00-trace-span-01");

        var headers = producer.Produced[0].Headers;
        Assert.Equal("correlation-123", HeaderValue(headers, "X-Correlation-Id"));
        Assert.Equal("00-trace-span-01", HeaderValue(headers, "traceparent"));
    }

    [Fact]
    public async Task AbsentCorrelationContext_AddsNoEmptyHeaders()
    {
        var producer = new RecordingProducer();

        await new KafkaOutboxDispatcher(producer).DispatchAsync("OrderEvent", "{}");

        Assert.Null(HeaderValue(producer.Produced[0].Headers, "X-Correlation-Id"));
        Assert.Null(HeaderValue(producer.Produced[0].Headers, "traceparent"));
    }

    [Fact]
    public void ANullProducer_IsRejectedAtConstruction()
    {
        Assert.Throws<ArgumentNullException>(() => new KafkaOutboxDispatcher(null!));
    }
}
