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

    // ---- Tenant attribution -------------------------------------------------------------------
    //
    // AuditLogEntry carried no tenant, so the audit collection had no way to say which tenant an
    // entry belonged to. Every entity in a multi-tenant application is tenant-scoped, so a read API
    // over the trail would have served one tenant's entity ids, operator ids, timestamps and
    // property diffs to another -- which is why the trail could be written but never safely read.

    private sealed class FixedTenantContext : Foundry.Core.Tenant.ITenantContext
    {
        private string? _tenantId;
        public FixedTenantContext(string? tenantId) => _tenantId = tenantId;
        public string? TenantId => _tenantId;
        public bool HasTenant => !string.IsNullOrEmpty(_tenantId);
        public void SetTenantId(string tenantId) => _tenantId = tenantId;
    }

    private sealed class RecordingSink : IAuditSink
    {
        public readonly List<AuditLogEntry> Written = new();

        public Task WriteAsync(AuditLogEntry entry, System.Threading.CancellationToken ct = default)
        {
            Written.Add(entry);
            return Task.CompletedTask;
        }

        public Task WriteManyAsync(IReadOnlyList<AuditLogEntry> entries, System.Threading.CancellationToken ct = default)
        {
            Written.AddRange(entries);
            return Task.CompletedTask;
        }
    }

    private sealed record AuditProbe : Foundry.Core.Entities.IEntity<ObjectId>
    {
        public ObjectId Id { get; init; } = ObjectId.GenerateNewId();
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
        public int Version { get; set; } = 1;
        public string Name { get; init; } = string.Empty;
    }

    // Written against Repository, not against EntityAuditService.
    //
    // The tests these replace constructed EntityAuditService directly and asserted it stamped the
    // tenant. It did. It was also never called by anything: Repository builds audit entries inline
    // and the service was dead code, so the tests passed while the shipped behaviour was broken,
    // and 3 of 2,090 audit rows in a real application carried a tenant. Exercising the type that
    // actually writes is the whole point.

    private Foundry.Mongo.Repositories.Repository<AuditProbe> RepositoryWith(
        RecordingSink sink, string? tenant)
        => new(_db, sink, userContext: null, encryptionProvider: null,
               collectionName: "AuditProbes", tenantContext: new FixedTenantContext(tenant));

    [Fact]
    public async Task AnInsertThroughTheRepositoryCarriesTheTenant()
    {
        var sink = new RecordingSink();
        await RepositoryWith(sink, "acme").InsertAsync(new AuditProbe { Name = "one" });

        Assert.NotEmpty(sink.Written);
        Assert.All(sink.Written, e => Assert.Equal("acme", e.TenantId));
    }

    [Fact]
    public async Task ABulkInsertStampsEveryEntryNotJustTheFirst()
    {
        var sink = new RecordingSink();
        await RepositoryWith(sink, "acme").BulkInsertAsync(
            new[] { new AuditProbe { Name = "a" }, new AuditProbe { Name = "b" }, new AuditProbe { Name = "c" } });

        Assert.Equal(3, sink.Written.Count);
        Assert.All(sink.Written, e => Assert.Equal("acme", e.TenantId));
    }

    [Fact]
    public async Task WithNoTenantContextTheEntryHasNoTenant()
    {
        // Null, never empty string: a tenant-scoped read excludes null, and "" would match a caller
        // whose own tenant is unset.
        var sink = new RecordingSink();
        await RepositoryWith(sink, null).InsertAsync(new AuditProbe { Name = "one" });

        Assert.NotEmpty(sink.Written);
        Assert.All(sink.Written, e => Assert.Null(e.TenantId));
    }

    [Fact]
    public async Task AnEntryWithNoTenantStillRoundTripsThroughMongo()
    {
        // Rows written before the field existed have no tenant. They must still deserialise.
        var sink = new Foundry.Mongo.Audit.MongoAuditSink(_db);
        var entityId = ObjectId.GenerateNewId().ToString();

        await sink.WriteAsync(AuditLogEntry.ForInsert("op-1", "Probe", entityId, "probes"));

        var collection = _db.GetCollection<AuditLogEntry>("audit_log");
        var read = await collection.Find(Builders<AuditLogEntry>.Filter.Eq(e => e.EntityId, entityId)).ToListAsync();

        Assert.Single(read);
        Assert.Null(read[0].TenantId);
        Assert.Equal("op-1", read[0].OperatorId);
    }

    [Fact]
    public async Task ATenantStampedEntryRoundTripsThroughMongo()
    {
        var sink = new Foundry.Mongo.Audit.MongoAuditSink(_db);
        var entityId = ObjectId.GenerateNewId().ToString();

        await sink.WriteAsync(
            AuditLogEntry.ForInsert("op-1", "Probe", entityId, "probes") with { TenantId = "acme" });

        var collection = _db.GetCollection<AuditLogEntry>("audit_log");
        var read = await collection
            .Find(Builders<AuditLogEntry>.Filter.Eq(e => e.TenantId, "acme"))
            .ToListAsync();

        Assert.Single(read);
        Assert.Equal(entityId, read[0].EntityId);
    }
}
