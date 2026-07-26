using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundry.Core.Audit;
using Foundry.Core.User;
using Foundry.Core.Entities;
using Foundry.Core.Entities;
using Foundry.Mongo.Repositories;
using MongoDB.Bson;
using MongoDB.Driver;
using NSubstitute;
using Xunit;

namespace Foundry.Mongo.Tests;

public class RepositoryAuditTests
{
    public record AuditedEntity : BaseEntity<ObjectId>
    {
        public string Description { get; set; } = string.Empty;
        public int Quantity { get; set; }
    }

    public record SoftDeleteEntity : BaseEntity<ObjectId>, ISoftDelete
    {
        public string Title { get; set; } = string.Empty;
        public bool IsDeleted { get; init; }
        public DateTime? DeletedAt { get; init; }
    }

    [Fact]
    public async Task InsertAsync_StampsEntity_And_EmitsAuditLog()
    {
        // Arrange
        var mockDb = Substitute.For<IMongoDatabase>();
        var mockCollection = Substitute.For<IMongoCollection<AuditedEntity>>();
        mockCollection.CollectionNamespace.Returns(new CollectionNamespace(new DatabaseNamespace("TestDb"), "AuditedEntities"));
        mockDb.GetCollection<AuditedEntity>(Arg.Any<string>()).Returns(mockCollection);

        var auditSink = new InMemoryAuditSink();
        var userContext = Substitute.For<ICurrentUserContext>();
        userContext.OperatorId.Returns("test-operator");

        var repository = new Repository<AuditedEntity>(mockDb, auditSink, userContext);
        var entity = new AuditedEntity { Id = ObjectId.GenerateNewId(), Description = "Test Item", Quantity = 5 };

        // Act
        await repository.InsertAsync(entity);

        // Assert
        Assert.Equal(1, entity.Version);
        Assert.True(entity.CreatedAtUtc > DateTime.UtcNow.AddSeconds(-5));
        Assert.Equal(entity.CreatedAtUtc, entity.UpdatedAtUtc);

        await mockCollection.Received(1).InsertOneAsync(entity, null, Arg.Any<CancellationToken>());

        var auditEntries = auditSink.GetEntries();
        Assert.Single(auditEntries);
        var entry = auditEntries[0];
        Assert.Equal("test-operator", entry.OperatorId);
        Assert.Equal(AuditAction.Inserted, entry.Action);
        Assert.Equal(entity.Id.ToString(), entry.EntityId);
        Assert.Equal("AuditedEntities", entry.CollectionName);
    }

    [Fact]
    public async Task UpdateAsync_CalculatesDelta_And_EmitsAuditLog()
    {
        // Arrange
        var mockDb = Substitute.For<IMongoDatabase>();
        var mockCollection = Substitute.For<IMongoCollection<AuditedEntity>>();
        mockCollection.CollectionNamespace.Returns(new CollectionNamespace(new DatabaseNamespace("TestDb"), "AuditedEntities"));
        mockDb.GetCollection<AuditedEntity>(Arg.Any<string>()).Returns(mockCollection);

        var existingEntity = new AuditedEntity
        {
            Id = ObjectId.Parse("507f1f77bcf86cd799439011"),
            Description = "Old Description",
            Quantity = 10,
            CreatedAtUtc = DateTime.UtcNow.AddDays(-1),
            UpdatedAtUtc = DateTime.UtcNow.AddDays(-1),
            Version = 1
        };

        // Mock FindAsync to return pre-image
        var cursor = new TestAsyncCursor<AuditedEntity>(existingEntity);
        mockCollection.FindAsync(
            Arg.Any<FilterDefinition<AuditedEntity>>(),
            Arg.Any<FindOptions<AuditedEntity, AuditedEntity>>(),
            Arg.Any<CancellationToken>()
        ).Returns(Task.FromResult<IAsyncCursor<AuditedEntity>>(cursor));

        // Mock ReplaceOneAsync to return success
        var replaceResult = Substitute.For<ReplaceOneResult>();
        replaceResult.MatchedCount.Returns(1);
        mockCollection.ReplaceOneAsync(
            Arg.Any<FilterDefinition<AuditedEntity>>(),
            Arg.Any<AuditedEntity>(),
            Arg.Any<ReplaceOptions>(),
            Arg.Any<CancellationToken>()
        ).Returns(Task.FromResult(replaceResult));

        var auditSink = new InMemoryAuditSink();
        var userContext = Substitute.For<ICurrentUserContext>();
        userContext.OperatorId.Returns("update-operator");

        var repository = new Repository<AuditedEntity>(mockDb, auditSink, userContext);

        // Updated entity state
        var updatedEntity = new AuditedEntity
        {
            Id = existingEntity.Id,
            Description = "New Description", // changed
            Quantity = 10,                   // unchanged
            Version = 1
        };

        // Act
        await repository.UpdateAsync(updatedEntity);

        // Assert
        Assert.Equal(2, updatedEntity.Version);
        Assert.Equal(existingEntity.CreatedAtUtc, updatedEntity.CreatedAtUtc);
        Assert.True(updatedEntity.UpdatedAtUtc > DateTime.UtcNow.AddSeconds(-5));

        var auditEntries = auditSink.GetEntries();
        Assert.Single(auditEntries);
        var entry = auditEntries[0];
        Assert.Equal("update-operator", entry.OperatorId);
        Assert.Equal(AuditAction.Updated, entry.Action);
        Assert.Equal(existingEntity.Id.ToString(), entry.EntityId);
        
        // Check property diffs
        Assert.Contains(entry.PropertyDiffs, d => d.PropertyName == "Description" && 
                                                 (string?)d.OldValue == "Old Description" && 
                                                 (string?)d.NewValue == "New Description");
        Assert.Contains(entry.PropertyDiffs, d => d.PropertyName == "Version" && 
                                                 (int?)d.OldValue == 1 && 
                                                 (int?)d.NewValue == 2);
    }

    [Fact]
    public async Task DeleteAsync_OnSoftDeleteEntity_SetsIsDeleted_And_EmitsAuditLog()
    {
        // Arrange
        var mockDb = Substitute.For<IMongoDatabase>();
        var mockCollection = Substitute.For<IMongoCollection<SoftDeleteEntity>>();
        mockCollection.CollectionNamespace.Returns(new CollectionNamespace(new DatabaseNamespace("TestDb"), "SoftDeleteEntities"));
        mockDb.GetCollection<SoftDeleteEntity>(Arg.Any<string>()).Returns(mockCollection);

        var entityId = ObjectId.Parse("507f1f77bcf86cd799439011");
        var existingEntity = new SoftDeleteEntity
        {
            Id = entityId,
            Title = "Active Item",
            IsDeleted = false,
            DeletedAt = null
        };

        var cursor = new TestAsyncCursor<SoftDeleteEntity>(existingEntity);
        mockCollection.FindAsync(
            Arg.Any<FilterDefinition<SoftDeleteEntity>>(),
            Arg.Any<FindOptions<SoftDeleteEntity, SoftDeleteEntity>>(),
            Arg.Any<CancellationToken>()
        ).Returns(Task.FromResult<IAsyncCursor<SoftDeleteEntity>>(cursor));

        var auditSink = new InMemoryAuditSink();
        var userContext = Substitute.For<ICurrentUserContext>();
        userContext.OperatorId.Returns("delete-operator");

        var repository = new Repository<SoftDeleteEntity>(mockDb, auditSink, userContext);

        // Act
        await repository.DeleteAsync(entityId);

        // Assert
        // Should call UpdateOneAsync to mark IsDeleted = true
        await mockCollection.Received(1).UpdateOneAsync(
            Arg.Any<FilterDefinition<SoftDeleteEntity>>(),
            Arg.Any<UpdateDefinition<SoftDeleteEntity>>(),
            Arg.Any<UpdateOptions>(),
            Arg.Any<CancellationToken>()
        );

        var auditEntries = auditSink.GetEntries();
        Assert.Single(auditEntries);
        var entry = auditEntries[0];
        Assert.Equal("delete-operator", entry.OperatorId);
        Assert.Equal(AuditAction.DeletedSoft, entry.Action);
        Assert.Equal(entityId.ToString(), entry.EntityId);
    }
}
