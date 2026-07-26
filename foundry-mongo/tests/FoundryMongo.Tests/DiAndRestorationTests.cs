using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundry.Mongo.DependencyInjection;
using Foundry.Core.Audit;
using Foundry.Core.Entities;
using Foundry.Core.Entities;
using Foundry.Core.Security;
using Foundry.Mongo.Repositories;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using NSubstitute;
using Xunit;

namespace Foundry.Mongo.Tests;

public class DiAndRestorationTests
{
    public record ActiveCustomer : BaseEntity<ObjectId>, ISoftDelete, IVersionable
    {
        public string Name { get; set; } = string.Empty;
        public bool IsDeleted { get; init; }
        public DateTime? DeletedAt { get; init; }
    }

    [Fact]
    public void AddFoundryMongo_RegistersCoreDependenciesCorrectly()
    {
        // Arrange
        var services = new ServiceCollection();
        var fakeKey = Convert.ToBase64String(new byte[32]);

        // Act
        services.AddFoundryMongo(options =>
        {
            options.ConnectionString = "mongodb://localhost:27017";
            options.DatabaseName = "DiTestDb";
            options.EncryptionKey = fakeKey;
        });

        var provider = services.BuildServiceProvider();

        // Assert - MongoClient and MongoDatabase resolved
        var mongoClient = provider.GetService<IMongoClient>();
        var mongoDatabase = provider.GetService<IMongoDatabase>();
        var encryptionProvider = provider.GetService<IEncryptionProvider>();
        var repository = provider.GetService<IRepository<ActiveCustomer>>();

        Assert.NotNull(mongoClient);
        Assert.NotNull(mongoDatabase);
        Assert.NotNull(encryptionProvider);
        Assert.NotNull(repository);

        Assert.Equal("DiTestDb", mongoDatabase.DatabaseNamespace.DatabaseName);
        Assert.IsType<Repository<ActiveCustomer>>(repository);
    }

    [Fact]
    public async Task RestoreDeletedAsync_RestoresSoftDeletedState_And_WritesAuditLog()
    {
        // Arrange
        var mockDb = Substitute.For<IMongoDatabase>();
        var mockCollection = Substitute.For<IMongoCollection<ActiveCustomer>>();
        mockCollection.CollectionNamespace.Returns(new CollectionNamespace(new DatabaseNamespace("TestDb"), "ActiveCustomers"));
        mockCollection.Database.Returns(mockDb);
        mockDb.GetCollection<ActiveCustomer>(Arg.Any<string>()).Returns(mockCollection);

        var entityId = ObjectId.Parse("507f1f77bcf86cd799439011");
        
        var deletedRecord = new ActiveCustomer
        {
            Id = entityId,
            Name = "Deleted Corp",
            IsDeleted = true,
            DeletedAt = DateTime.UtcNow.AddDays(-1),
            Version = 2
        };

        // Mock pre-image fetch
        var cursor = new TestAsyncCursor<ActiveCustomer>(deletedRecord);
        mockCollection.FindAsync(
            Arg.Any<FilterDefinition<ActiveCustomer>>(),
            Arg.Any<FindOptions<ActiveCustomer, ActiveCustomer>>(),
            Arg.Any<CancellationToken>()
        ).Returns(Task.FromResult<IAsyncCursor<ActiveCustomer>>(cursor));

        // Mock ReplaceOne success
        var replaceResult = Substitute.For<ReplaceOneResult>();
        replaceResult.MatchedCount.Returns(1);

        ActiveCustomer? capturedRestoredEntity = null;
        mockCollection.ReplaceOneAsync(
            Arg.Any<FilterDefinition<ActiveCustomer>>(),
            Arg.Do<ActiveCustomer>(c => capturedRestoredEntity = c),
            Arg.Any<ReplaceOptions>(),
            Arg.Any<CancellationToken>()
        ).Returns(Task.FromResult(replaceResult));

        var auditSink = new InMemoryAuditSink();
        var repository = new Repository<ActiveCustomer>(mockDb, auditSink);

        // Act
        await repository.RestoreDeletedAsync(entityId);

        // Assert - Flags cleared and version incremented
        Assert.NotNull(capturedRestoredEntity);
        Assert.False(capturedRestoredEntity.IsDeleted);
        Assert.Null(capturedRestoredEntity.DeletedAt);
        Assert.Equal(3, capturedRestoredEntity.Version);

        // Assert - Audit Log entry has Restored action
        var entries = auditSink.GetEntries();
        Assert.Single(entries);
        var entry = entries[0];
        Assert.Equal(AuditAction.Restored, entry.Action);
        
        var isDeletedDiff = entry.PropertyDiffs.FirstOrDefault(d => d.PropertyName == "IsDeleted");
        Assert.NotNull(isDeletedDiff.PropertyName);
        Assert.Equal(true, isDeletedDiff.OldValue);
        Assert.Equal(false, isDeletedDiff.NewValue);
    }
}
