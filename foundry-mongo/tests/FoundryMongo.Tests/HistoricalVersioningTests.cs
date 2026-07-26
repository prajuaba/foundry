using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundry.Core.Entities;
using Foundry.Mongo.Repositories;
using MongoDB.Bson;
using MongoDB.Driver;
using NSubstitute;
using Xunit;

namespace Foundry.Mongo.Tests;

public class HistoricalVersioningTests
{
    public record VersionedProduct : BaseEntity<ObjectId>, IVersionable
    {
        public string Sku { get; set; } = string.Empty;
        public int Price { get; set; }
    }

    [Fact]
    public async Task Insert_And_Update_OnVersionedEntity_WritesRevisions()
    {
        // Arrange
        var mockDb = Substitute.For<IMongoDatabase>();
        var mockCollection = Substitute.For<IMongoCollection<VersionedProduct>>();
        mockCollection.CollectionNamespace.Returns(new CollectionNamespace(new DatabaseNamespace("TestDb"), "VersionedProducts"));
        mockCollection.Database.Returns(mockDb);
        mockDb.GetCollection<VersionedProduct>(Arg.Any<string>()).Returns(mockCollection);

        var mockHistoryCollection = Substitute.For<IMongoCollection<EntityRevision>>();
        mockDb.GetCollection<EntityRevision>("VersionedProducts_History").Returns(mockHistoryCollection);

        var repository = new Repository<VersionedProduct>(mockDb);
        var product = new VersionedProduct { Id = ObjectId.GenerateNewId(), Sku = "VP-1", Price = 100 };

        // Act - Insert
        await repository.InsertAsync(product);

        // Assert - Insert revision written
        await mockHistoryCollection.Received(1).InsertOneAsync(
            Arg.Is<EntityRevision>(r => r.EntityId == product.Id.ToString() && r.Version == 1 && r.Action == "Insert"),
            Arg.Any<InsertOneOptions>(),
            Arg.Any<CancellationToken>()
        );

        // Mock database pre-image find for update
        var existingProduct = new VersionedProduct { Id = product.Id, Sku = "VP-1", Price = 100, Version = 1 };
        var cursor = new TestAsyncCursor<VersionedProduct>(existingProduct);
        mockCollection.FindAsync(
            Arg.Any<FilterDefinition<VersionedProduct>>(),
            Arg.Any<FindOptions<VersionedProduct, VersionedProduct>>(),
            Arg.Any<CancellationToken>()
        ).Returns(Task.FromResult<IAsyncCursor<VersionedProduct>>(cursor));

        // Mock ReplaceOne success
        var replaceResult = Substitute.For<ReplaceOneResult>();
        replaceResult.MatchedCount.Returns(1);
        mockCollection.ReplaceOneAsync(
            Arg.Any<FilterDefinition<VersionedProduct>>(),
            Arg.Any<VersionedProduct>(),
            Arg.Any<ReplaceOptions>(),
            Arg.Any<CancellationToken>()
        ).Returns(Task.FromResult(replaceResult));

        // Act - Update price
        product.Price = 120;
        await repository.UpdateAsync(product);

        // Assert - Update revision written
        await mockHistoryCollection.Received(1).InsertOneAsync(
            Arg.Is<EntityRevision>(r => r.EntityId == product.Id.ToString() && r.Version == 2 && r.Action == "Update"),
            Arg.Any<InsertOneOptions>(),
            Arg.Any<CancellationToken>()
        );
    }

    [Fact]
    public async Task RestoreVersionAsync_OverwritesWithHistoricalSnapshot_And_WritesRestoreRevision()
    {
        // Arrange
        var mockDb = Substitute.For<IMongoDatabase>();
        var mockCollection = Substitute.For<IMongoCollection<VersionedProduct>>();
        mockCollection.CollectionNamespace.Returns(new CollectionNamespace(new DatabaseNamespace("TestDb"), "VersionedProducts"));
        mockCollection.Database.Returns(mockDb);
        mockDb.GetCollection<VersionedProduct>(Arg.Any<string>()).Returns(mockCollection);

        var mockHistoryCollection = Substitute.For<IMongoCollection<EntityRevision>>();
        mockDb.GetCollection<EntityRevision>("VersionedProducts_History").Returns(mockHistoryCollection);

        var entityId = ObjectId.Parse("507f1f77bcf86cd799439011");
        
        // Target historical version 1 data snapshot
        var historicalProduct = new VersionedProduct { Id = entityId, Sku = "VP-Original", Price = 80, Version = 1 };
        var historicalDoc = historicalProduct.ToBsonDocument();
        var historicalRevision = new EntityRevision
        {
            EntityId = entityId.ToString(),
            Version = 1,
            Data = historicalDoc,
            Action = "Insert"
        };

        // Mock GetRevisionByVersionAsync finding version 1
        var historyCursor = new TestAsyncCursor<EntityRevision>(historicalRevision);
        mockHistoryCollection.FindAsync(
            Arg.Any<FilterDefinition<EntityRevision>>(),
            Arg.Any<FindOptions<EntityRevision, EntityRevision>>(),
            Arg.Any<CancellationToken>()
        ).Returns(Task.FromResult<IAsyncCursor<EntityRevision>>(historyCursor));

        // Mock current database state (version 2)
        var currentDbProduct = new VersionedProduct { Id = entityId, Sku = "VP-Modified", Price = 150, Version = 2 };
        var cursor = new TestAsyncCursor<VersionedProduct>(currentDbProduct);
        mockCollection.FindAsync(
            Arg.Any<FilterDefinition<VersionedProduct>>(),
            Arg.Any<FindOptions<VersionedProduct, VersionedProduct>>(),
            Arg.Any<CancellationToken>()
        ).Returns(Task.FromResult<IAsyncCursor<VersionedProduct>>(cursor));

        var repository = new Repository<VersionedProduct>(mockDb);

        // Act
        var restored = await repository.RestoreVersionAsync(entityId, 1);

        // Assert
        Assert.NotNull(restored);
        Assert.Equal("VP-Original", restored.Sku);
        Assert.Equal(80, restored.Price);
        // Restored document is saved as next version (v3)
        Assert.Equal(3, restored.Version);

        // Assert ReplaceOneAsync called to write the restored state to main collection
        await mockCollection.Received(1).ReplaceOneAsync(
            Arg.Any<FilterDefinition<VersionedProduct>>(),
            Arg.Is<VersionedProduct>(p => p.Sku == "VP-Original" && p.Price == 80 && p.Version == 3),
            Arg.Is<ReplaceOptions>(o => o.IsUpsert == true),
            Arg.Any<CancellationToken>()
        );

        // Assert new revision written with action "Restore (v1)" at version 3
        await mockHistoryCollection.Received(1).InsertOneAsync(
            Arg.Is<EntityRevision>(r => r.EntityId == entityId.ToString() && r.Version == 3 && r.Action == "Restore (v1)"),
            null,
            Arg.Any<CancellationToken>()
        );
    }
}
