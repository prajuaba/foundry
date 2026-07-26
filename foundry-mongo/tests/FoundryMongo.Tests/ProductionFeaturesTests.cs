using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundry.Core.Entities;
using Foundry.Mongo.Repositories;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using NSubstitute;
using Xunit;

namespace Foundry.Mongo.Tests;

public class ProductionFeaturesTests
{
    public record IndexedEntity : BaseEntity<ObjectId>
    {
        [Indexed(Unique = true)]
        public string Sku { get; init; } = string.Empty;

        [Indexed(Descending = true)]
        public int Stock { get; init; }

        [TextIndexed]
        public string Title { get; init; } = string.Empty;

        [TextIndexed]
        public string Description { get; init; } = string.Empty;
    }

    [Fact]
    public async Task UpdateAsync_WithOlderVersion_ThrowsConcurrencyException()
    {
        // Arrange
        var mockDb = Substitute.For<IMongoDatabase>();
        var mockCollection = Substitute.For<IMongoCollection<IndexedEntity>>();
        mockCollection.CollectionNamespace.Returns(new CollectionNamespace(new DatabaseNamespace("TestDb"), "IndexedEntities"));
        mockDb.GetCollection<IndexedEntity>(Arg.Any<string>()).Returns(mockCollection);

        var entityId = ObjectId.Parse("507f1f77bcf86cd799439011");
        
        // Database contains version 2
        var databaseEntity = new IndexedEntity
        {
            Id = entityId,
            Sku = "A100",
            Version = 2
        };

        var cursor = new TestAsyncCursor<IndexedEntity>(databaseEntity);
        mockCollection.FindAsync(
            Arg.Any<FilterDefinition<IndexedEntity>>(),
            Arg.Any<FindOptions<IndexedEntity, IndexedEntity>>(),
            Arg.Any<CancellationToken>()
        ).Returns(Task.FromResult<IAsyncCursor<IndexedEntity>>(cursor));

        // When replacing, return MatchedCount = 0 (simulating OCC failure)
        var replaceResult = Substitute.For<ReplaceOneResult>();
        replaceResult.MatchedCount.Returns(0);
        
        mockCollection.ReplaceOneAsync(
            Arg.Any<FilterDefinition<IndexedEntity>>(),
            Arg.Any<IndexedEntity>(),
            Arg.Any<ReplaceOptions>(),
            Arg.Any<CancellationToken>()
        ).Returns(Task.FromResult(replaceResult));

        // Simulating the exists check returning 1 (document exists, but versions mismatch)
        mockCollection.CountDocumentsAsync(
            Arg.Any<FilterDefinition<IndexedEntity>>(),
            Arg.Any<CountOptions>(),
            Arg.Any<CancellationToken>()
        ).Returns(Task.FromResult(1L));

        var repository = new Repository<IndexedEntity>(mockDb);
        
        // Client tries to save changes based on stale version 1
        var clientEntity = new IndexedEntity
        {
            Id = entityId,
            Sku = "A100-Stale",
            Version = 1
        };

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ConcurrencyException>(() => repository.UpdateAsync(clientEntity));
        Assert.Equal(entityId.ToString(), exception.EntityId);
        Assert.Equal("IndexedEntities", exception.CollectionName);
    }

    [Fact]
    public async Task Repository_PassesSession_ToDriverOperations()
    {
        // Arrange
        var mockDb = Substitute.For<IMongoDatabase>();
        var mockCollection = Substitute.For<IMongoCollection<IndexedEntity>>();
        mockCollection.CollectionNamespace.Returns(new CollectionNamespace(new DatabaseNamespace("TestDb"), "IndexedEntities"));
        mockDb.GetCollection<IndexedEntity>(Arg.Any<string>()).Returns(mockCollection);

        var repository = new Repository<IndexedEntity>(mockDb);
        var mockSession = Substitute.For<IClientSessionHandle>();
        var entity = new IndexedEntity { Id = ObjectId.GenerateNewId(), Sku = "B200" };

        // Act
        await repository.InsertAsync(entity, mockSession);

        // Assert
        await mockCollection.Received(1).InsertOneAsync(
            mockSession,
            entity,
            Arg.Any<InsertOneOptions>(),
            Arg.Any<CancellationToken>()
        );
    }

    [Fact]
    public async Task CreateIndexesAsync_BuildsAndRegistersCorrectIndexes()
    {
        // Arrange
        var mockDb = Substitute.For<IMongoDatabase>();
        var mockCollection = Substitute.For<IMongoCollection<IndexedEntity>>();
        var mockIndexManager = Substitute.For<IMongoIndexManager<IndexedEntity>>();
        
        mockCollection.Indexes.Returns(mockIndexManager);
        mockCollection.CollectionNamespace.Returns(new CollectionNamespace(new DatabaseNamespace("TestDb"), "IndexedEntities"));
        mockDb.GetCollection<IndexedEntity>(Arg.Any<string>()).Returns(mockCollection);

        var repository = new Repository<IndexedEntity>(mockDb);
        
        List<CreateIndexModel<IndexedEntity>>? registeredModels = null;
        mockIndexManager.CreateManyAsync(
            Arg.Do<IEnumerable<CreateIndexModel<IndexedEntity>>>(models => registeredModels = models.ToList()),
            Arg.Any<CreateManyIndexesOptions>(),
            Arg.Any<CancellationToken>()
        ).Returns(Task.FromResult<IEnumerable<string>>(new List<string>()));

        // Act
        await repository.CreateIndexesAsync();

        // Assert
        Assert.NotNull(registeredModels);
        // We expect 3 indexes: 
        // 1. Sku (ascending + unique)
        // 2. Stock (descending)
        // 3. Combined TextIndex for Title and Description
        Assert.Equal(3, registeredModels.Count);

        var skuIndex = registeredModels.FirstOrDefault(m => 
            m.Options.Unique == true && 
            m.Keys.Render(new RenderArgs<IndexedEntity>(
                BsonSerializer.SerializerRegistry.GetSerializer<IndexedEntity>(), 
                BsonSerializer.SerializerRegistry
            )).ToString().ToLowerInvariant().Contains("sku"));
        Assert.NotNull(skuIndex);

        var stockIndex = registeredModels.FirstOrDefault(m => 
            m.Keys.Render(new RenderArgs<IndexedEntity>(
                BsonSerializer.SerializerRegistry.GetSerializer<IndexedEntity>(), 
                BsonSerializer.SerializerRegistry
            )).ToString().ToLowerInvariant().Contains("stock"));
        Assert.NotNull(stockIndex);

        var textIndex = registeredModels.FirstOrDefault(m => m.Options.Name == "TextIndex");
        Assert.NotNull(textIndex);
        var renderedTextKeys = textIndex.Keys.Render(new RenderArgs<IndexedEntity>(
            BsonSerializer.SerializerRegistry.GetSerializer<IndexedEntity>(), 
            BsonSerializer.SerializerRegistry
        ));
        var renderedTextKeysStr = renderedTextKeys.ToString().ToLowerInvariant();
        Assert.Contains("title", renderedTextKeysStr);
        Assert.Contains("description", renderedTextKeysStr);
    }
}
