using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Foundry.Core.Audit;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace Foundry.Mongo.Tests;

public class MongoAuditSinkTests : IDisposable
{
    private readonly string _dbName;
    private readonly MongoClient _client;
    private readonly IMongoDatabase _db;

    public MongoAuditSinkTests()
    {
        // Register MongoDB conventions once per test
        Foundry.Mongo.Infrastructure.Conventions.MongoDbConventions.Register();

        _dbName = $"AuditTest_{Guid.NewGuid().ToString().Substring(0, 8)}";
        _client = new MongoClient("mongodb://localhost:27017");
        _db = _client.GetDatabase(_dbName);
    }

    public void Dispose()
    {
        // Clean up the test database
        try
        {
            _client.DropDatabase(_dbName);
        }
        catch
        {
            // Ignore cleanup failures
        }
    }

    [Fact]
    public async Task AWrittenEntryCanBeReadBack()
    {
        // Arrange
        var sink = new Foundry.Mongo.Audit.MongoAuditSink(_db);
        var operatorId = "test-operator-123";
        var entityType = "TestEntity";
        var entityId = "entity-id-456";
        var collectionName = "TestEntities";
        var action = AuditAction.Inserted;

        var entry = new AuditLogEntry
        {
            OperatorId = operatorId,
            EntityType = entityType,
            EntityId = entityId,
            CollectionName = collectionName,
            Action = action
        };

        // Act
        await sink.WriteAsync(entry);

        // Assert - Read directly from MongoDB
        var collection = _db.GetCollection<AuditLogEntry>("audit_log");
        var readEntries = await collection.Find(Builders<AuditLogEntry>.Filter.Empty).ToListAsync();

        Assert.Single(readEntries);
        var readEntry = readEntries[0];
        Assert.Equal(operatorId, readEntry.OperatorId);
        Assert.Equal(entityType, readEntry.EntityType);
        Assert.Equal(entityId, readEntry.EntityId);
        Assert.Equal(collectionName, readEntry.CollectionName);
        Assert.Equal(action, readEntry.Action);
        Assert.True(readEntry.TimestampUtc > DateTime.UtcNow.AddSeconds(-5));
    }

    [Fact]
    public async Task EveryEntryInABatchIsWritten()
    {
        // Arrange
        var sink = new Foundry.Mongo.Audit.MongoAuditSink(_db);
        var entries = new List<AuditLogEntry>
        {
            new()
            {
                OperatorId = "op1",
                EntityType = "Customer",
                EntityId = "cust-001",
                CollectionName = "Customers",
                Action = AuditAction.Inserted
            },
            new()
            {
                OperatorId = "op2",
                EntityType = "Order",
                EntityId = "order-002",
                CollectionName = "Orders",
                Action = AuditAction.Updated
            },
            new()
            {
                OperatorId = "op3",
                EntityType = "Product",
                EntityId = "prod-003",
                CollectionName = "Products",
                Action = AuditAction.DeletedSoft
            }
        };

        // Act
        await sink.WriteManyAsync(entries);

        // Assert - Verify all entries are in MongoDB
        var collection = _db.GetCollection<AuditLogEntry>("audit_log");
        var readEntries = await collection.Find(Builders<AuditLogEntry>.Filter.Empty).ToListAsync();

        Assert.Equal(3, readEntries.Count);
        Assert.Contains(readEntries, e => e.OperatorId == "op1" && e.EntityId == "cust-001");
        Assert.Contains(readEntries, e => e.OperatorId == "op2" && e.EntityId == "order-002");
        Assert.Contains(readEntries, e => e.OperatorId == "op3" && e.EntityId == "prod-003");
    }

    [Fact]
    public async Task ABatchWithOneInvalidEntryWritesNothing()
    {
        // Arrange
        var sink = new Foundry.Mongo.Audit.MongoAuditSink(_db);
        var entries = new List<AuditLogEntry>
        {
            new()
            {
                OperatorId = "valid-op",
                EntityType = "Customer",
                EntityId = "cust-001",
                CollectionName = "Customers",
                Action = AuditAction.Inserted
            },
            new()
            {
                OperatorId = "", // Invalid: empty OperatorId
                EntityType = "Order",
                EntityId = "order-002",
                CollectionName = "Orders",
                Action = AuditAction.Updated
            }
        };

        // Act & Assert - All-or-nothing: should throw and write nothing
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sink.WriteManyAsync(entries)
        );
        Assert.Contains("OperatorId", ex.Message);

        // Verify NO documents were written to MongoDB
        var collection = _db.GetCollection<AuditLogEntry>("audit_log");
        var readEntries = await collection.Find(Builders<AuditLogEntry>.Filter.Empty).ToListAsync();
        Assert.Empty(readEntries);
    }

    [Fact]
    public async Task AnEntryWithNoOperatorIsRefused()
    {
        // Arrange
        var sink = new Foundry.Mongo.Audit.MongoAuditSink(_db);
        var entry = new AuditLogEntry
        {
            OperatorId = "", // Empty
            EntityType = "TestEntity",
            EntityId = "id-123",
            CollectionName = "TestEntities",
            Action = AuditAction.Inserted
        };

        // Act & Assert
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sink.WriteAsync(entry)
        );
        Assert.Contains("OperatorId", ex.Message);
    }

    [Fact]
    public async Task AnEntryWithNoEntityTypeIsRefused()
    {
        // Arrange
        var sink = new Foundry.Mongo.Audit.MongoAuditSink(_db);
        var entry = new AuditLogEntry
        {
            OperatorId = "op-123",
            EntityType = "", // Empty
            EntityId = "id-456",
            CollectionName = "TestEntities",
            Action = AuditAction.Inserted
        };

        // Act & Assert
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sink.WriteAsync(entry)
        );
        Assert.Contains("entity type", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheDeclaredIndexesExistAfterTheFirstWrite()
    {
        // Arrange
        var sink = new Foundry.Mongo.Audit.MongoAuditSink(_db);
        var entry = new AuditLogEntry
        {
            OperatorId = "op-1",
            EntityType = "Test",
            EntityId = "id-1",
            CollectionName = "Tests",
            Action = AuditAction.Inserted
        };

        // Act - Trigger index creation
        await sink.WriteAsync(entry);

        // Assert - Verify indexes exist
        var collection = _db.GetCollection<AuditLogEntry>("audit_log");
        var indexes = await collection.Indexes.ListAsync();
        var indexList = await indexes.ToListAsync();

        // Should have compound index: EntityType, EntityId (ascending), TimestampUtc (descending)
        var compoundIndexFound = indexList.Any(idx =>
        {
            var keys = idx["key"].AsBsonDocument;
            return keys.Contains("entityType") &&
                   keys.Contains("entityId") &&
                   keys.Contains("timestampUtc");
        });
        Assert.True(compoundIndexFound, "Compound index (EntityType, EntityId, TimestampUtc) not found");

        // Should have timestamp index: TimestampUtc (descending)
        var timestampIndexFound = indexList.Any(idx =>
        {
            var keys = idx["key"].AsBsonDocument;
            return keys.ElementCount == 1 && keys.Contains("timestampUtc");
        });
        Assert.True(timestampIndexFound, "Timestamp index (TimestampUtc) not found");
    }

    [Fact]
    public async Task ACustomCollectionNameIsHonoured()
    {
        // Arrange
        const string customCollectionName = "custom_audit_trail";
        var sink = new Foundry.Mongo.Audit.MongoAuditSink(_db, customCollectionName);
        var entry = new AuditLogEntry
        {
            OperatorId = "op-1",
            EntityType = "Entity",
            EntityId = "id-123",
            CollectionName = "Entities",
            Action = AuditAction.Inserted
        };

        // Act
        await sink.WriteAsync(entry);

        // Assert - Document should be in custom collection
        var customCollection = _db.GetCollection<AuditLogEntry>(customCollectionName);
        var customEntries = await customCollection.Find(Builders<AuditLogEntry>.Filter.Empty).ToListAsync();
        Assert.Single(customEntries);
        Assert.Equal("op-1", customEntries[0].OperatorId);

        // Assert - Default collection should be empty
        var defaultCollection = _db.GetCollection<AuditLogEntry>("audit_log");
        var defaultEntries = await defaultCollection.Find(Builders<AuditLogEntry>.Filter.Empty).ToListAsync();
        Assert.Empty(defaultEntries);
    }
}
