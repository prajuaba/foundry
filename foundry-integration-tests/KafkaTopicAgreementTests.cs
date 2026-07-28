using System;
using System.Threading;
using System.Threading.Tasks;
using Foundry.Core.Attributes;
using Foundry.Core.Outbox;
using Foundry.Kafka.Bridge;
using Foundry.Schema.Compiler;
using NSubstitute;
using Xunit;

namespace Foundry.IntegrationTests;

/// <summary>
/// The topic a schema declares, and the topic the publisher writes to, are the same topic.
/// </summary>
/// <remarks>
/// <para>
/// They were not. A schema could declare <c>kafkaTopic</c>, and the compiler used that name for
/// exactly one thing — the topic the generated consumer subscribes to. Nothing carried it to the
/// publishing side: <c>KafkaOutboxDispatcher</c> derived a topic from the event type alone and
/// <c>OutboxMessage</c> had nowhere to record one. Declaring <c>"kafkaTopic": "orders.v2"</c>
/// produced a consumer listening on <c>orders.v2</c> and a publisher writing to
/// <c>order-events</c>. <strong>The consumer received nothing and both halves reported
/// success.</strong>
/// </para>
/// <para>
/// This project is where the check belongs because it is the only one that references both the
/// compiler and <c>Foundry.Kafka</c>. The compiler has no project references at all, so its copy of
/// the naming rule cannot be a call into the real one — and the previous attempt at holding two
/// copies together, a table duplicated on each side, is what let the route rule drift five separate
/// times. Comparing the implementations directly is what a table cannot do.
/// </para>
/// </remarks>
public class KafkaTopicAgreementTests
{
    [KafkaTopic("orders.v2")]
    private sealed record DeclaredTopicEntity;

    private sealed record UndeclaredTopicEntity;

    private sealed record PurchaseOrder;

    // ── The compiler's default and the dispatcher's default are one rule ─────

    [Theory]
    [InlineData("Order")]
    [InlineData("PurchaseOrder")]
    [InlineData("Invoice")]
    [InlineData("APIKey")]
    [InlineData("CustomerAccountNote")]
    public void TheCompilerDerivesTheTopicTheDispatcherPublishesTo(string entityName)
    {
        // Both sides lower-cased the whole name at one point; the dispatcher was changed to
        // kebab-case and the compiler's two copies were not. A single-word entity agreed by luck,
        // which is why nothing showed: every schema in this repository uses single-word names.
        var compiler = KafkaTopicNaming.Default(entityName);
        var runtime = KafkaOutboxDispatcher.ResolveTopicName(
            $"Foundry.Core.Outbox.EntityMutationEvent`1[[{entityName}, Asm]], Asm");

        Assert.Equal(runtime, compiler);
    }

    // ── A declared topic reaches the publisher ──────────────────────────────

    [Fact]
    public void EnqueuingRecordsTheDeclaredTopicOnTheMessage()
    {
        var topic = KafkaTopicDeclaration.For(typeof(EntityMutationEvent<DeclaredTopicEntity>));

        Assert.Equal("orders.v2", topic);
    }

    [Fact]
    public void EnqueuingRecordsNothingWhenTheEntityDeclaresNoTopic()
    {
        // Null, not a derived name: deriving here would put a second copy of the naming rule in the
        // queue, and the dispatcher is where that rule lives.
        var topic = KafkaTopicDeclaration.For(typeof(EntityMutationEvent<UndeclaredTopicEntity>));

        Assert.Null(topic);
    }

    [Fact]
    public async Task TheDispatcherPublishesToTheTopicTheMessageCarries()
    {
        var producer = Substitute.For<Foundry.Kafka.Producer.IKafkaProducer>();
        var dispatcher = new KafkaOutboxDispatcher(producer);

        await dispatcher.DispatchAsync(
            eventType: typeof(EntityMutationEvent<DeclaredTopicEntity>).AssemblyQualifiedName!,
            payload: "{}",
            topic: "orders.v2");

        await producer.Received(1).ProduceAsync(
            "orders.v2",
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<Confluent.Kafka.Headers>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheDispatcherFallsBackToItsOwnNamingWhenNoTopicIsCarried()
    {
        var producer = Substitute.For<Foundry.Kafka.Producer.IKafkaProducer>();
        var dispatcher = new KafkaOutboxDispatcher(producer);

        await dispatcher.DispatchAsync(
            eventType: typeof(EntityMutationEvent<PurchaseOrder>).AssemblyQualifiedName!,
            payload: "{}");

        await producer.Received(1).ProduceAsync(
            "purchase-order-events",
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<Confluent.Kafka.Headers>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ADeclaredTopicIsRefusedIfKafkaWouldRejectIt()
    {
        // The declared name gets the same character check as a derived one. Skipping it would put an
        // illegal name straight to the broker, which refuses it with "Broker: Invalid topic" naming
        // neither the topic nor the event -- and the worker retries that forever while reporting
        // nothing, so the first symptom is a queue that never drains.
        var dispatcher = new KafkaOutboxDispatcher(Substitute.For<Foundry.Kafka.Producer.IKafkaProducer>());

        var error = await Assert.ThrowsAsync<ArgumentException>(() => dispatcher.DispatchAsync(
            eventType: typeof(EntityMutationEvent<PurchaseOrder>).AssemblyQualifiedName!,
            payload: "{}",
            topic: "orders/v2"));

        Assert.Contains("orders/v2", error.Message);
    }
}
