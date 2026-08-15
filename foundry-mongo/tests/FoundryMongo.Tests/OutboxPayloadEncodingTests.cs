using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Foundry.Core.Outbox;
using Foundry.Core.Serialization;
using Foundry.Mongo.Repositories;
using Foundry.Mongo.Services;
using MongoDB.Bson;
using NSubstitute;
using Xunit;

namespace Foundry.Mongo.Tests;

/// <summary>
/// One entity left the application encoded two incompatible ways.
/// </summary>
/// <remarks>
/// <para>
/// <c>MongoOutboxQueue</c> serialized event payloads with a bare
/// <c>JsonSerializer.Serialize(eventData)</c> — no options — so the outbox got stock
/// System.Text.Json behaviour while the REST surface applied
/// <see cref="FoundryJsonDefaults.Options"/>. The same record therefore went out one shape over HTTP
/// and another over Kafka.
/// </para>
/// <para>
/// For <see cref="ObjectId"/> the Kafka shape was not merely different, it was unusable:
/// <c>{"Timestamp":1786762297,"CreationTime":"…"}</c> is System.Text.Json writing the struct's two
/// public members, both derived from the same four bytes of the id. The random and counter bytes are
/// absent, so nothing can turn that back into the id — and a consumer that cannot recover the id
/// cannot correlate the event to the record it describes, which is most of what an entity-change
/// event is for.
/// </para>
/// <para>
/// This was invisible to the existing round-trip suite because its fixtures carry <c>string</c> ids.
/// The chain was proven; the encoding travelling down it was not.
/// </para>
/// </remarks>
public class OutboxPayloadEncodingTests
{
    public enum Fulfilment
    {
        Pending = 0,
        Dispatched = 1,
        Delivered = 2,
    }

    public sealed record OrderPlaced(ObjectId Id, ObjectId CustomerId, Fulfilment Status, string Reference);

    private static async Task<string> EnqueueAndCapture<TEvent>(TEvent payload) where TEvent : class
    {
        var repository = Substitute.For<IRepository<OutboxMessage>>();
        OutboxMessage? captured = null;

        await repository.InsertAsync(
            Arg.Do<OutboxMessage>(m => captured = m),
            Arg.Any<MongoDB.Driver.IClientSessionHandle>(),
            Arg.Any<CancellationToken>());

        await new MongoOutboxQueue(repository).EnqueueAsync(payload, CancellationToken.None);

        Assert.NotNull(captured);
        return captured!.Payload;
    }

    [Fact]
    public async Task AnObjectIdIsPublishedAsItsHexString()
    {
        var id = ObjectId.GenerateNewId();

        var payload = await EnqueueAndCapture(
            new OrderPlaced(id, ObjectId.GenerateNewId(), Fulfilment.Pending, "ORD-1"));

        using var document = JsonDocument.Parse(payload);
        Assert.Equal(id.ToString(), document.RootElement.GetProperty("Id").GetString());
    }

    [Fact]
    public async Task TheStructMembersNeverAppearOnTheWire()
    {
        // The precise shape of the defect. Timestamp and CreationTime are what System.Text.Json
        // emits when no converter is registered, and their presence means the id is unrecoverable.
        var payload = await EnqueueAndCapture(
            new OrderPlaced(ObjectId.GenerateNewId(), ObjectId.GenerateNewId(), Fulfilment.Pending, "ORD-2"));

        Assert.DoesNotContain("CreationTime", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Timestamp\"", payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryObjectIdOnTheEventIsEncodedTheSameWay()
    {
        // Not just the key: a foreign key is the field a consumer most often needs to join on.
        var id = ObjectId.GenerateNewId();
        var customerId = ObjectId.GenerateNewId();

        var payload = await EnqueueAndCapture(new OrderPlaced(id, customerId, Fulfilment.Pending, "ORD-3"));

        using var document = JsonDocument.Parse(payload);
        Assert.Equal(id.ToString(), document.RootElement.GetProperty("Id").GetString());
        Assert.Equal(customerId.ToString(), document.RootElement.GetProperty("CustomerId").GetString());
    }

    [Fact]
    public async Task AnEnumIsPublishedByName()
    {
        // Ordinals make the wire format depend on declaration order, so inserting a value into an
        // enum in the schema silently changes the meaning of every message already published.
        var payload = await EnqueueAndCapture(
            new OrderPlaced(ObjectId.GenerateNewId(), ObjectId.GenerateNewId(), Fulfilment.Dispatched, "ORD-4"));

        using var document = JsonDocument.Parse(payload);
        Assert.Equal("Dispatched", document.RootElement.GetProperty("Status").GetString());
    }

    [Fact]
    public async Task ThePayloadRoundTripsBackToTheEventItCameFrom()
    {
        // The property that actually matters to a consumer, asserted end to end rather than field
        // by field: what was published can be read back as what was sent.
        var original = new OrderPlaced(
            ObjectId.GenerateNewId(), ObjectId.GenerateNewId(), Fulfilment.Delivered, "ORD-5");

        var payload = await EnqueueAndCapture(original);

        var restored = JsonSerializer.Deserialize<OrderPlaced>(payload, FoundryJsonDefaults.Options);

        Assert.Equal(original, restored);
    }

    [Fact]
    public async Task TheKafkaEncodingMatchesTheRestEncoding()
    {
        // The defect stated as one assertion: the two doors out of the application agree.
        var order = new OrderPlaced(
            ObjectId.GenerateNewId(), ObjectId.GenerateNewId(), Fulfilment.Dispatched, "ORD-6");

        var overKafka = await EnqueueAndCapture(order);
        var overRest = JsonSerializer.Serialize(order, FoundryJsonDefaults.Options);

        Assert.Equal(overRest, overKafka);
    }

    [Fact]
    public void ARowWrittenBeforeThisChangeStillDrains()
    {
        // A queue holding legacy rows must not start throwing on deserialization -- the publisher
        // would stall on the first one and never reach the rest. The id cannot be recovered, because
        // those bytes were never written; ObjectId.Empty is the honest answer and not an exception.
        const string legacy = """
        {"Id":{"Timestamp":1786762297,"CreationTime":"2026-08-15T02:51:37Z"},
         "CustomerId":{"Timestamp":1786762297,"CreationTime":"2026-08-15T02:51:37Z"},
         "Status":1,"Reference":"ORD-LEGACY"}
        """;

        var restored = JsonSerializer.Deserialize<OrderPlaced>(legacy, FoundryJsonDefaults.Options);

        Assert.NotNull(restored);
        Assert.Equal(ObjectId.Empty, restored!.Id);
        Assert.Equal("ORD-LEGACY", restored.Reference);
    }
}
