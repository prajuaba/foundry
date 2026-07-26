using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Foundry.Core.Outbox;
using Foundry.Core.Entities;
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
            Payload = JsonSerializer.Serialize(eventData),
            CreatedAt = DateTime.UtcNow,
            CorrelationId = correlationId,
            TraceParent = traceParent
        };

        await _repository.InsertAsync(outboxMessage, null, ct);
    }
}
