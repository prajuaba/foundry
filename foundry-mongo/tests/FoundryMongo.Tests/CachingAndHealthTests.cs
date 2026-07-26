using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Foundry.Mongo.Diagnostics;
using Foundry.Core.Entities;
using Foundry.Mongo.Repositories;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using MongoDB.Bson;
using MongoDB.Driver;
using NSubstitute;
using Xunit;

namespace Foundry.Mongo.Tests;

public class CachingAndHealthTests
{
    public record CachedCustomer : BaseEntity<ObjectId>
    {
        public string Name { get; set; } = string.Empty;
    }

    [Fact]
    public async Task GetByIdAsync_ReadsFromCache_OnSecondCall_And_InvalidatesOnUpdate()
    {
        // Arrange
        var mockDb = Substitute.For<IMongoDatabase>();
        var mockCollection = Substitute.For<IMongoCollection<CachedCustomer>>();
        mockCollection.CollectionNamespace.Returns(new CollectionNamespace(new DatabaseNamespace("TestDb"), "CachedCustomers"));
        mockCollection.Database.Returns(mockDb);
        mockDb.GetCollection<CachedCustomer>(Arg.Any<string>()).Returns(mockCollection);

        var innerRepository = new Repository<CachedCustomer>(mockDb);
        var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var cachedRepository = new CachedRepository<CachedCustomer>(innerRepository, memoryCache);

        var customerId = ObjectId.GenerateNewId();
        var customerRecord = new CachedCustomer { Id = customerId, Name = "Alice", Version = 1 };

        // Mock pre-image fetch from inner DB (returns fresh cursor each time to prevent exhaustion)
        mockCollection.FindAsync(
            Arg.Any<FilterDefinition<CachedCustomer>>(),
            Arg.Any<FindOptions<CachedCustomer, CachedCustomer>>(),
            Arg.Any<CancellationToken>()
        ).Returns(x => Task.FromResult<IAsyncCursor<CachedCustomer>>(new TestAsyncCursor<CachedCustomer>(customerRecord)));

        // Act - Call 1 (Cache Miss, should call inner MongoCollection FindAsync)
        var result1 = await cachedRepository.GetByIdAsync(customerId);
        Assert.NotNull(result1);
        Assert.Equal("Alice", result1.Name);

        // Act - Call 2 (Cache Hit, should NOT call inner MongoCollection FindAsync again)
        var result2 = await cachedRepository.GetByIdAsync(customerId);
        Assert.NotNull(result2);
        Assert.Equal("Alice", result2.Name);

        // Assert - Inner collection received FindAsync exactly 1 time due to cache hit
        await mockCollection.Received(1).FindAsync(
            Arg.Any<FilterDefinition<CachedCustomer>>(),
            Arg.Any<FindOptions<CachedCustomer, CachedCustomer>>(),
            Arg.Any<CancellationToken>()
        );

        // Mock ReplaceOne success
        var replaceResult = Substitute.For<ReplaceOneResult>();
        replaceResult.MatchedCount.Returns(1);
        mockCollection.ReplaceOneAsync(
            Arg.Any<FilterDefinition<CachedCustomer>>(),
            Arg.Any<CachedCustomer>(),
            Arg.Any<ReplaceOptions>(),
            Arg.Any<CancellationToken>()
        ).Returns(Task.FromResult(replaceResult));

        // Act - Mutation (Updates record, which must invalidate the cache key)
        var updatedRecord = new CachedCustomer { Id = customerId, Name = "Alice Updated", Version = 1 };
        await cachedRepository.UpdateAsync(updatedRecord);

        // Mock third pre-image fetch with updated record (returning version 2 after increment)
        var databasePostImage = new CachedCustomer { Id = customerId, Name = "Alice Updated", Version = 2 };
        mockCollection.FindAsync(
            Arg.Any<FilterDefinition<CachedCustomer>>(),
            Arg.Any<FindOptions<CachedCustomer, CachedCustomer>>(),
            Arg.Any<CancellationToken>()
        ).Returns(x => Task.FromResult<IAsyncCursor<CachedCustomer>>(new TestAsyncCursor<CachedCustomer>(databasePostImage)));

        // Act - Call 3 (Should trigger a cache miss because of update invalidation, fetching from db again)
        var result3 = await cachedRepository.GetByIdAsync(customerId);
        Assert.NotNull(result3);
        Assert.Equal("AliceUpdated", result3.Name.Replace(" ", ""));

        // Assert - Inner collection received FindAsync a second time now (1 for call 1 + 1 for call 3)
        // Note: UpdateAsync also calls FindAsync for pre-image fetch, so total FindAsync calls should be 3
        await mockCollection.Received(3).FindAsync(
            Arg.Any<FilterDefinition<CachedCustomer>>(),
            Arg.Any<FindOptions<CachedCustomer, CachedCustomer>>(),
            Arg.Any<CancellationToken>()
        );
    }

    [Fact]
    public async Task MongoDbHealthCheck_ReturnsHealthy_WhenPingSucceeds()
    {
        // Arrange
        var mockDb = Substitute.For<IMongoDatabase>();
        mockDb.RunCommandAsync<BsonDocument>(
            Arg.Any<Command<BsonDocument>>(),
            Arg.Any<ReadPreference>(),
            Arg.Any<CancellationToken>()
        ).Returns(Task.FromResult(new BsonDocument("ok", 1)));

        var healthCheck = new MongoDbHealthCheck(mockDb);
        var context = new HealthCheckContext
        {
            Registration = new HealthCheckRegistration("MongoHealth", healthCheck, HealthStatus.Unhealthy, null)
        };

        // Act
        var result = await healthCheck.CheckHealthAsync(context);

        // Assert
        Assert.Equal(HealthStatus.Healthy, result.Status);
    }
}
