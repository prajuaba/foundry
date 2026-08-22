using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using NSubstitute;
using Xunit;
using Foundry.Core.Outbox;
using Foundry.Core.Entities;
using Foundry.Mongo.Services;
using Foundry.Mongo.Repositories;
using Foundry.Api.MediatR;
using Foundry.Api.MediatR.Behaviors;
using Paperclip.OrderingSystem.Domain;
using MediatR;

namespace Foundry.IntegrationTests;

public class OutboxTests
{
    /// <summary>
    /// Declares the opt-in, as a generated entity would. MongoOutboxQueue drops any event type
    /// that has not, so without this the queue writes nothing and the assertion below fails on
    /// an empty collection rather than on anything to do with what it is testing.
    /// </summary>
    [Foundry.Core.Attributes.KafkaOutbox]
    public class SampleDomainEvent
    {
        public string EventId { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
    }

    [Fact]
    public async Task OutboxQueue_InsertsMessage_Successfully()
    {
        // Arrange
        var mockDb = Substitute.For<IMongoDatabase>();
        var mockCollection = Substitute.For<IMongoCollection<OutboxMessage>>();
        mockCollection.CollectionNamespace.Returns(new CollectionNamespace(new DatabaseNamespace("TestDb"), "OutboxMessages"));
        mockCollection.Database.Returns(mockDb);
        mockDb.GetCollection<OutboxMessage>(Arg.Any<string>()).Returns(mockCollection);

        var repository = new Repository<OutboxMessage>(mockDb, null, null, null);
        var queue = new MongoOutboxQueue(repository);

        OutboxMessage? capturedMessage = null;
        await mockCollection.InsertOneAsync(
            Arg.Do<OutboxMessage>(m => capturedMessage = m),
            Arg.Any<InsertOneOptions>(),
            Arg.Any<CancellationToken>()
        );

        var sampleEvent = new SampleDomainEvent { EventId = "evt-1", Content = "Order Created" };

        // Act
        await queue.EnqueueAsync(sampleEvent, CancellationToken.None);

        // Assert
        Assert.NotNull(capturedMessage);
        Assert.Contains("SampleDomainEvent", capturedMessage.EventType);
        Assert.Contains("evt-1", capturedMessage.Payload);
        Assert.Null(capturedMessage.ProcessedAt);
        Assert.Equal(0, capturedMessage.RetryCount);
    }

    [Fact]
    public async Task OutboxPublisherWorker_DispatchesAndMarksAsProcessed()
    {
        // Arrange
        var mockDb = Substitute.For<IMongoDatabase>();
        var mockCollection = Substitute.For<IMongoCollection<OutboxMessage>>();
        mockCollection.CollectionNamespace.Returns(new CollectionNamespace(new DatabaseNamespace("TestDb"), "OutboxMessages"));
        mockCollection.Database.Returns(mockDb);
        mockDb.GetCollection<OutboxMessage>(Arg.Any<string>()).Returns(mockCollection);

        // Set up mock database to return our outbox message
        var outboxMessage = new OutboxMessage
        {
            Id = ObjectId.GenerateNewId(),
            EventType = typeof(SampleDomainEvent).AssemblyQualifiedName ?? nameof(SampleDomainEvent),
            Payload = "{\"EventId\":\"evt-2\",\"Content\":\"Dispatched\"}",
            CreatedAt = DateTime.UtcNow,
            RetryCount = 0
        };

        mockCollection.FindAsync(
            Arg.Any<FilterDefinition<OutboxMessage>>(),
            Arg.Any<FindOptions<OutboxMessage, OutboxMessage>>(),
            Arg.Any<CancellationToken>()
        ).Returns(x => 
        {
            var freshCursor = Substitute.For<IAsyncCursor<OutboxMessage>>();
            freshCursor.Current.Returns(new List<OutboxMessage> { outboxMessage });
            int internalCount = 0;
            freshCursor.MoveNextAsync(Arg.Any<CancellationToken>()).Returns(_ => 
            {
                internalCount++;
                return Task.FromResult(internalCount == 1);
            });
            return Task.FromResult(freshCursor);
        });

        var repository = new Repository<OutboxMessage>(mockDb, null, null, null);
        var dispatcher = Substitute.For<IOutboxDispatcher>();

        var services = new ServiceCollection();
        services.AddSingleton<IRepository<OutboxMessage>>(repository);
        services.AddSingleton<IOutboxDispatcher>(dispatcher);
        var serviceProvider = services.BuildServiceProvider();

        var worker = new OutboxPublisherWorker(serviceProvider, NullLogger<OutboxPublisherWorker>.Instance);

        // The worker claims a message before publishing it, so this has to answer the claim. Two
        // workers previously selected the same row and both published it; the claim is a
        // find-and-update that only one can win.
        mockCollection.FindOneAndUpdateAsync(
            Arg.Any<FilterDefinition<OutboxMessage>>(),
            Arg.Any<UpdateDefinition<OutboxMessage>>(),
            Arg.Any<FindOneAndUpdateOptions<OutboxMessage, OutboxMessage>>(),
            Arg.Any<CancellationToken>()
        ).Returns(Task.FromResult(outboxMessage));

        // Marking is now a field update rather than a whole-document replace, so that a failed
        // attempt cannot overwrite another worker's successful mark.
        UpdateDefinition<OutboxMessage>? appliedUpdate = null;
        var updateResult = Substitute.For<UpdateResult>();
        updateResult.MatchedCount.Returns(1);
        updateResult.ModifiedCount.Returns(1);

        mockCollection.UpdateOneAsync(
            Arg.Any<FilterDefinition<OutboxMessage>>(),
            Arg.Any<UpdateDefinition<OutboxMessage>>(),
            Arg.Any<UpdateOptions>(),
            Arg.Any<CancellationToken>()
        ).Returns(x =>
        {
            appliedUpdate = x.ArgAt<UpdateDefinition<OutboxMessage>>(1);
            return Task.FromResult(updateResult);
        });

        // Act
        // Invoke protected ProcessOutboxMessagesAsync via reflection to run a single sweep
        var method = typeof(OutboxPublisherWorker).GetMethod("ProcessOutboxMessagesAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);
        
        var task = (Task)method.Invoke(worker, new object[] { CancellationToken.None })!;
        await task;

        // Assert
        await dispatcher.Received(1).DispatchAsync(
            outboxMessage.EventType,
            outboxMessage.Payload,
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>()
        );

        Assert.NotNull(appliedUpdate);

        var rendered = appliedUpdate!.Render(new MongoDB.Driver.RenderArgs<OutboxMessage>(
            MongoDB.Bson.Serialization.BsonSerializer.SerializerRegistry.GetSerializer<OutboxMessage>(),
            MongoDB.Bson.Serialization.BsonSerializer.SerializerRegistry));

        // Marked processed, and nothing else touched: no retry bookkeeping on a successful publish.
        var set = rendered["$set"].AsBsonDocument;
        Assert.True(set.Contains("processedAt") || set.Contains("ProcessedAt"));
        Assert.False(set.Contains("retryCount") || set.Contains("RetryCount"));
    }

    /// <summary>
    /// An entity that declares a sensitive property, which <c>Order</c> does not.
    /// </summary>
    /// <remarks>
    /// The distinction is the whole point. <see cref="SensitiveFieldRedactor.Redact"/> returns the
    /// original instance when a type declares nothing sensitive, and a copy with the property
    /// emptied when it does. Every existing assertion about this behaviour used <c>Order</c>, so
    /// redaction was never reached: the code path under test returned its input and the test
    /// compared the input to itself.
    /// </remarks>
    public record CardHolder : BaseEntity<ObjectId>
    {
        public string Name { get; set; } = string.Empty;

        [Foundry.Core.Entities.SensitiveData(
            Protection = Foundry.Core.Entities.ProtectionType.Encrypt)]
        public string CardNumber { get; set; } = string.Empty;
    }

    [Fact]
    public async Task OutboxDomainEventBehavior_EmptiesASensitiveFieldBeforeEnqueueing()
    {
        // The gate this file did not have.
        //
        // OutboxDomainEventBehavior calls SensitiveFieldRedactor.Redact at exactly one place, and
        // deleting that call passed every suite in the repository -- 1,400-odd tests -- while
        // putting fields declared Encrypt onto a Kafka topic in plaintext. The redactor itself has
        // four unit tests; nothing asserted that the only production caller uses it. That is the
        // same shape as the real-time tenant gap: the policy is covered, the path that calls it
        // is not.
        var queue = Substitute.For<IOutboxQueue>();
        var behavior = new OutboxDomainEventBehavior<InsertCommand<CardHolder>, CardHolder>(queue);

        const string CardNumber = "4111111111111111";
        var holder = new CardHolder
        {
            Id = MongoDB.Bson.ObjectId.GenerateNewId(),
            Name = "A. Holder",
            CardNumber = CardNumber
        };

        RequestHandlerDelegate<CardHolder> next = () => Task.FromResult(holder);
        await behavior.Handle(new InsertCommand<CardHolder>(holder), next, CancellationToken.None);

        await queue.Received(1).EnqueueAsync(
            Arg.Is<EntityMutationEvent<CardHolder>>(e =>
                e.MutationType == "Insert"
                && e.Entity.CardNumber == string.Empty
                && e.Entity.Name == "A. Holder"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OutboxDomainEventBehavior_DoesNotEmptyTheCallersOwnCopy()
    {
        // Redaction is for what leaves the process. The caller handed this object to the command
        // and still holds it, and the repository has yet to encrypt and persist it -- blanking it
        // in place would destroy the value on its way to storage rather than protect it on its way
        // to a broker. Redact clones for this reason, and that is worth an assertion, because the
        // cheapest way to make the test above pass is to empty the property in place.
        var queue = Substitute.For<IOutboxQueue>();
        var behavior = new OutboxDomainEventBehavior<InsertCommand<CardHolder>, CardHolder>(queue);

        const string CardNumber = "4111111111111111";
        var holder = new CardHolder { Id = MongoDB.Bson.ObjectId.GenerateNewId(), CardNumber = CardNumber };

        RequestHandlerDelegate<CardHolder> next = () => Task.FromResult(holder);
        await behavior.Handle(new InsertCommand<CardHolder>(holder), next, CancellationToken.None);

        Assert.Equal(CardNumber, holder.CardNumber);
    }

    [Fact]
    public async Task OutboxDomainEventBehavior_EnqueuesMutationEvent_OnSuccess()
    {
        // Arrange
        var mockOutboxQueue = Substitute.For<IOutboxQueue>();
        var behavior = new OutboxDomainEventBehavior<InsertCommand<Order>, Order>(mockOutboxQueue);

        var order = new Order { Id = MongoDB.Bson.ObjectId.GenerateNewId(), OrderNumber = "ORD-OUTBOX-1" };
        var command = new InsertCommand<Order>(order);

        RequestHandlerDelegate<Order> nextDelegate = () => Task.FromResult(order);

        // Act
        var result = await behavior.Handle(command, nextDelegate, CancellationToken.None);

        // Assert
        //
        // 'e.Entity == order' is reference equality, and it holds only because Order declares no
        // sensitive property, so Redact returns its input untouched. Read as a statement about
        // redaction it is backwards -- it asserts the entity reaches the outbox unchanged, which
        // is the failure the two tests above exist to catch. Kept, and narrowed to what it can
        // honestly claim: an entity with nothing to redact passes through.
        Assert.Equal(order, result);
        await mockOutboxQueue.Received(1).EnqueueAsync(
            Arg.Is<EntityMutationEvent<Order>>(e =>
                e.MutationType == "Insert" &&
                e.Entity == order
            ),
            Arg.Any<CancellationToken>()
        );
    }
}
