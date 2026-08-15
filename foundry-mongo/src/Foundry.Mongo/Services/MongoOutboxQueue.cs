using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Foundry.Core.Outbox;
using Foundry.Core.Entities;
using Foundry.Core.Serialization;
using MongoDB.Bson;
using Foundry.Mongo.Repositories;

namespace Foundry.Mongo.Services;

/// <summary>
/// MongoDB-backed implementation of IOutboxQueue that stores event messages in an OutboxMessage collection.
/// </summary>
public class MongoOutboxQueue : IOutboxQueue
{
    private readonly IRepository<OutboxMessage> _repository;

    /// <summary>
    /// Initializes a new instance of the <see cref="MongoOutboxQueue"/> class.
    /// </summary>
    /// <param name="repository">The MongoDB repository for OutboxMessage.</param>
    public MongoOutboxQueue(IRepository<OutboxMessage> repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    /// <inheritdoc />
    public async Task EnqueueAsync<TEvent>(TEvent eventData, CancellationToken ct) where TEvent : class
    {
        if (eventData == null) throw new ArgumentNullException(nameof(eventData));

        var currentActivity = System.Diagnostics.Activity.Current;
        var correlationId = currentActivity?.RootId ?? Guid.NewGuid().ToString();
        var traceParent = currentActivity?.Id;

        var outboxMessage = new OutboxMessage
        {
            Id = ObjectId.GenerateNewId(),
            EventType = typeof(TEvent).AssemblyQualifiedName ?? typeof(TEvent).Name,

            // FoundryJsonDefaults, the same options the REST surface uses. This was a bare
            // JsonSerializer.Serialize(eventData) with no options, so the outbox got stock
            // System.Text.Json behaviour and one entity left the application encoded two
            // incompatible ways depending on which door it went out of.
            //
            // An ObjectId reached Kafka as {"Timestamp":1786762297,"CreationTime":"..."} -- STJ
            // writing the struct's public members -- where REST returned
            // "6a7fd439e14b0c841d8ac77a". Both of those members derive from the same four bytes of
            // the id, and the random and counter bytes are absent, so the value cannot be turned
            // back into an ObjectId at all: a consumer could not correlate an event to the record
            // it describes, which is most of what an entity-change event is for. Enums went out as
            // ordinals, so reordering values in a schema silently changed the meaning of every
            // message already published.
            //
            // ObjectIdJsonConverter.Read still accepts an object shape, so a queue holding rows
            // written before this change drains rather than throwing. Those rows cannot recover
            // their ids -- the bytes were never written -- and decode to ObjectId.Empty.
            Payload = JsonSerializer.Serialize(eventData, FoundryJsonDefaults.Options),

            // Null unless the entity declares [KafkaTopic], in which case the message records where
            // it belongs. Without this the declaration reached the generated consumer and nothing
            // else, so a schema naming its own topic produced a consumer subscribed where the
            // publisher never wrote.
            Topic = KafkaTopicDeclaration.For(typeof(TEvent)),
            CreatedAt = DateTime.UtcNow,
            CorrelationId = correlationId,
            TraceParent = traceParent
        };

        await _repository.InsertAsync(outboxMessage, null, ct);
    }
}
