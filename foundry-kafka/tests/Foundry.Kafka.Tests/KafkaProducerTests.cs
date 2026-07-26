using Confluent.Kafka;
using Foundry.Kafka.Configuration;
using Foundry.Kafka.Producer;
using Microsoft.Extensions.Options;
using Xunit;

namespace Foundry.Kafka.Tests;

/// <summary>
/// Configuration validation and lifetime behaviour of <see cref="KafkaProducer"/>.
/// </summary>
public class KafkaProducerTests
{
    private sealed class FakeProducer : IProducer<string, string>
    {
        public bool Disposed { get; private set; }

        public Handle Handle => throw new NotSupportedException();
        public string Name => "fake";

        public void Dispose() => Disposed = true;

        public Task<DeliveryResult<string, string>> ProduceAsync(
            string topic, Message<string, string> message, CancellationToken ct = default)
            => Task.FromResult(new DeliveryResult<string, string> { Topic = topic, Message = message });

        public Task<DeliveryResult<string, string>> ProduceAsync(
            TopicPartition topicPartition, Message<string, string> message, CancellationToken ct = default)
            => Task.FromResult(new DeliveryResult<string, string> { Message = message });

        public void Produce(string topic, Message<string, string> message,
            Action<DeliveryReport<string, string>>? deliveryHandler = null) { }

        public void Produce(TopicPartition topicPartition, Message<string, string> message,
            Action<DeliveryReport<string, string>>? deliveryHandler = null) { }

        public int Flush(TimeSpan timeout) => 0;
        public void Flush(CancellationToken ct = default) { }
        public int Poll(TimeSpan timeout) => 0;
        public void InitTransactions(TimeSpan timeout) { }
        public void BeginTransaction() { }
        public void CommitTransaction(TimeSpan timeout) { }
        public void CommitTransaction() { }
        public void AbortTransaction(TimeSpan timeout) { }
        public void AbortTransaction() { }
        public void SendOffsetsToTransaction(
            IEnumerable<TopicPartitionOffset> offsets, IConsumerGroupMetadata groupMetadata, TimeSpan timeout) { }
        public int AddBrokers(string brokers) => 0;
        public void SetSaslCredentials(string username, string password) { }
    }

    private static IOptions<KafkaOptions> Options(Action<KafkaOptions>? configure = null)
    {
        var options = new KafkaOptions { BootstrapServers = "localhost:9092" };
        configure?.Invoke(options);
        return Microsoft.Extensions.Options.Options.Create(options);
    }

    // ---- configuration validation ----

    [Fact]
    public void MissingBootstrapServers_FailsAtConstructionNotAtFirstPublish()
    {
        // Left unvalidated, an unconfigured producer builds and the application starts cleanly; the
        // failure surfaces inside librdkafka at the first publish, by which point outbox rows are
        // quietly accumulating unpublished.
        var options = Microsoft.Extensions.Options.Options.Create(new KafkaOptions());

        var error = Assert.Throws<InvalidOperationException>(() => new KafkaProducer(options));
        Assert.Contains("BootstrapServers", error.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-1)]
    public void ValidAcksValues_AreAccepted(int acks)
    {
        Assert.Equal((Acks)acks, KafkaProducer.ParseAcks(acks));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(-2)]
    public void InvalidAcksValues_AreRejected(int acks)
    {
        // Acks.All is -1, so 2 and 3 look reasonable and are not. Casting them produced an undefined
        // enum member and an unpredictable durability setting on the outbox path.
        var error = Assert.Throws<InvalidOperationException>(() => KafkaProducer.ParseAcks(acks));
        Assert.Contains("Acks", error.Message);
    }

    // ---- lifetime ----

    [Fact]
    public async Task ProduceAsync_ForwardsTopicKeyValueAndHeaders()
    {
        var inner = new FakeProducer();
        using var producer = new KafkaProducer(inner);
        var headers = new Headers { { "X-Correlation-Id", System.Text.Encoding.UTF8.GetBytes("abc") } };

        var result = await producer.ProduceAsync("orders-events", "key-1", "payload", headers);

        Assert.Equal("orders-events", result.Topic);
        Assert.Equal("key-1", result.Message.Key);
        Assert.Equal("payload", result.Message.Value);
        Assert.NotNull(result.Message.Headers);
    }

    [Fact]
    public void Dispose_DisposesTheUnderlyingProducer()
    {
        var inner = new FakeProducer();
        new KafkaProducer(inner).Dispose();

        Assert.True(inner.Disposed);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var producer = new KafkaProducer(new FakeProducer());

        producer.Dispose();
        producer.Dispose();
    }

    [Fact]
    public async Task UseAfterDispose_ThrowsRatherThanSilentlyDroppingTheMessage()
    {
        // A publish against a disposed producer must not appear to succeed: the outbox marks a row
        // processed on a successful dispatch, so a swallowed send is a permanently lost event.
        var producer = new KafkaProducer(new FakeProducer());
        producer.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => producer.ProduceAsync("t", "k", "v"));
        Assert.Throws<ObjectDisposedException>(() => producer.Produce("t", "k", "v"));
        Assert.Throws<ObjectDisposedException>(() => producer.Flush(TimeSpan.Zero));
    }
}
