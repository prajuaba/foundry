using Confluent.Kafka;
using Foundry.Kafka.Configuration;
using Xunit;

namespace Foundry.Kafka.Tests;

/// <summary>
/// Default Kafka configuration, judged against what a transactional outbox requires.
/// </summary>
/// <remarks>
/// An outbox exists to guarantee that a committed domain mutation eventually reaches consumers.
/// Whether that guarantee holds is decided almost entirely by these defaults, and a weak default
/// loses messages only under broker failure — never in development, never in a demo, and without
/// any error at the point of loss.
/// </remarks>
public class KafkaOptionsTests
{
    [Fact]
    public void Acks_DefaultsToAllReplicas()
    {
        // Acks=1 (Leader) acknowledges as soon as the partition leader has the message. If that
        // broker fails before replicating, the message is gone -- while the producer has already
        // been told the publish succeeded and the outbox row has been marked processed. That
        // combination breaks the at-least-once guarantee the outbox is for, so durability is the
        // right default and throughput the thing a deployment opts into.
        Assert.Equal((int)Acks.All, new ProducerOptions().Acks);
    }

    [Fact]
    public void TheAcksDefault_IsAValidConfluentAcksValue()
    {
        // The value is cast straight to the Acks enum, where All is -1 rather than a large positive
        // number, so a plausible-looking integer can produce an invalid enum.
        Assert.True(Enum.IsDefined(typeof(Acks), (Acks)new ProducerOptions().Acks));
    }

    [Theory]
    [InlineData(0)]     // None
    [InlineData(1)]     // Leader
    [InlineData(-1)]    // All
    public void SupportedAcksValues_MapToKnownEnumMembers(int acks)
    {
        Assert.True(Enum.IsDefined(typeof(Acks), (Acks)acks));
    }

    [Fact]
    public void CompressionType_DefaultParsesToAKnownValue()
    {
        Assert.True(Enum.TryParse<CompressionType>(new ProducerOptions().CompressionType, true, out _));
    }

    [Fact]
    public void ConsumerDefaults_ArePresent()
    {
        var consumer = new ConsumerOptions();

        Assert.True(Enum.TryParse<AutoOffsetReset>(consumer.AutoOffsetReset, true, out _));
        Assert.True(consumer.SessionTimeoutMs > 0);
        Assert.NotNull(consumer.TopicApiMappings);
    }

    [Fact]
    public void KafkaOptions_ExposeInitialisedSubOptions()
    {
        // Both are dereferenced without a null check when the producer is built.
        var options = new KafkaOptions();

        Assert.NotNull(options.ProducerOptions);
        Assert.NotNull(options.ConsumerOptions);
    }
}
