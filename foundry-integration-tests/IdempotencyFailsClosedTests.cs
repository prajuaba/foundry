using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using MediatR;
using Xunit;
using Foundry.Api.MediatR;
using Foundry.Api.MediatR.Behaviors;
using Foundry.Api.Middleware;
using Paperclip.OrderingSystem.Domain;

namespace Foundry.IntegrationTests;

/// <summary>
/// What idempotency does when its store is unavailable.
/// </summary>
/// <remarks>
/// <para>
/// The distributed cache failures were caught, logged at warning, and stepped over. Carrying on
/// there does not degrade the feature — it silently removes it. The in-memory cache is per instance,
/// so behind more than one replica the distributed cache is the only thing that can see a duplicate:
/// a cache outage turned "at most once" into "at least once" while every request still returned 200.
/// For the operation this exists to protect, that is a double charge.
/// </para>
/// <para>
/// The asymmetry below is the design. Before the command runs, refusing is the answer the caller can
/// act on — a 409 is retryable, a duplicate payment is not. After it has run, failing the response
/// would make the caller retry, and the retry is precisely what would execute it a second time.
/// </para>
/// </remarks>
public class IdempotencyFailsClosedTests
{
    private const string Key = "idem-key-1";

    /// <summary>A distributed cache that is down in the way a real one is: every call throws.</summary>
    private sealed class UnavailableCache : IDistributedCache
    {
        public byte[]? Get(string key) => throw new InvalidOperationException("cache unavailable");
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default)
            => throw new InvalidOperationException("cache unavailable");
        public void Refresh(string key) => throw new InvalidOperationException("cache unavailable");
        public Task RefreshAsync(string key, CancellationToken token = default)
            => throw new InvalidOperationException("cache unavailable");
        public void Remove(string key) => throw new InvalidOperationException("cache unavailable");
        public Task RemoveAsync(string key, CancellationToken token = default)
            => throw new InvalidOperationException("cache unavailable");
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
            => throw new InvalidOperationException("cache unavailable");
        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
            => throw new InvalidOperationException("cache unavailable");
    }

    /// <summary>Reads fail; writes succeed. Isolates the read guard from the write guard.</summary>
    private sealed class ReadOnlyFailingCache : IDistributedCache
    {
        public byte[]? Get(string key) => throw new InvalidOperationException("cache unavailable");
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default)
            => throw new InvalidOperationException("cache unavailable");
        public void Refresh(string key) { }
        public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;
        public void Remove(string key) { }
        public Task RemoveAsync(string key, CancellationToken token = default) => Task.CompletedTask;
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) { }
        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
            => Task.CompletedTask;
    }

    /// <summary>Reads succeed and return nothing; writes fail. Isolates the in-flight marker.</summary>
    private sealed class WriteOnlyFailingCache : IDistributedCache
    {
        public byte[]? Get(string key) => null;
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => Task.FromResult<byte[]?>(null);
        public void Refresh(string key) { }
        public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;
        public void Remove(string key) { }
        public Task RemoveAsync(string key, CancellationToken token = default) => Task.CompletedTask;
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
            => throw new InvalidOperationException("cache unavailable");
        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
            => throw new InvalidOperationException("cache unavailable");
    }

    /// <summary>Reads and the first write succeed; only the completed marker fails.</summary>
    private sealed class CompletedWriteFailingCache : IDistributedCache
    {
        private int _writes;

        public byte[]? Get(string key) => null;
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => Task.FromResult<byte[]?>(null);
        public void Refresh(string key) { }
        public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;
        public void Remove(string key) { }
        public Task RemoveAsync(string key, CancellationToken token = default) => Task.CompletedTask;
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) { }

        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
            => Interlocked.Increment(ref _writes) == 1
                ? Task.CompletedTask
                : throw new InvalidOperationException("cache unavailable");
    }

    private static (IdempotencyBehavior<InsertCommand<Order>, Order> Behavior, InsertCommand<Order> Command)
        Build(IDistributedCache distributed)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-Idempotency-Key"] = Key;

        var behavior = new IdempotencyBehavior<InsertCommand<Order>, Order>(
            new MemoryCache(new MemoryCacheOptions()),
            new HttpContextAccessor { HttpContext = httpContext },
            Microsoft.Extensions.Logging.Abstractions.NullLogger<IdempotencyBehavior<InsertCommand<Order>, Order>>.Instance,
            distributed);

        var order = new Order { Id = MongoDB.Bson.ObjectId.GenerateNewId(), OrderNumber = "ORD-1" };

        return (behavior, new InsertCommand<Order>(order));
    }

    [Fact]
    public async Task AnUnreadableStoreRefusesTheRequestRatherThanRunningItUnprotected()
    {
        // Reads fail and writes succeed, so only the read guard can produce this refusal. Written
        // against a cache where everything failed first, and that version passed with the read guard
        // reverted -- the in-flight write guard downstream was catching it, and the test proved a
        // rule it was not exercising.
        var (behavior, command) = Build(new ReadOnlyFailingCache());

        var executions = 0;
        var error = await Assert.ThrowsAsync<IdempotencyException>(() =>
            behavior.Handle(command, () => { executions++; return Task.FromResult(command.Entity); }, CancellationToken.None));

        Assert.Equal(Key, error.IdempotencyKey);
        Assert.Contains("unavailable", error.Message);

        // The load-bearing assertion: the command did not run. Warning-and-continuing executed it.
        Assert.Equal(0, executions);
    }

    [Fact]
    public async Task AStoreThatFailsEntirelyRefusesTheRequest()
    {
        // Both guards apply; this is the shape a real outage takes.
        var (behavior, command) = Build(new UnavailableCache());

        var executions = 0;
        await Assert.ThrowsAsync<IdempotencyException>(() =>
            behavior.Handle(command, () => { executions++; return Task.FromResult(command.Entity); }, CancellationToken.None));

        Assert.Equal(0, executions);
    }

    [Fact]
    public async Task AnUnwritableStoreRefusesBeforeTheCommandRuns()
    {
        // The in-flight marker is what a concurrent duplicate on another replica sees. Unrecorded,
        // the two run alongside each other.
        var (behavior, command) = Build(new WriteOnlyFailingCache());

        var executions = 0;
        await Assert.ThrowsAsync<IdempotencyException>(() =>
            behavior.Handle(command, () => { executions++; return Task.FromResult(command.Entity); }, CancellationToken.None));

        Assert.Equal(0, executions);
    }

    [Fact]
    public async Task AFailureRecordingCompletionDoesNotFailTheCommandThatAlreadySucceeded()
    {
        // The other half, and the reason this is not "fail closed everywhere": the command has run.
        // Throwing now would make the caller retry, and the retry is what runs it twice.
        var (behavior, command) = Build(new CompletedWriteFailingCache());

        var executions = 0;
        var response = await behavior.Handle(
            command, () => { executions++; return Task.FromResult(command.Entity); }, CancellationToken.None);

        Assert.Equal(1, executions);
        Assert.Same(command.Entity, response);
    }

    [Fact]
    public async Task WithNoDistributedCacheConfiguredNothingChanges()
    {
        // A single-instance deployment registers no distributed cache at all. Refusing there would
        // turn an optional dependency into a required one.
        var (behavior, command) = Build(distributed: null!);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-Idempotency-Key"] = Key;

        var solo = new IdempotencyBehavior<InsertCommand<Order>, Order>(
            new MemoryCache(new MemoryCacheOptions()),
            new HttpContextAccessor { HttpContext = httpContext },
            Microsoft.Extensions.Logging.Abstractions.NullLogger<IdempotencyBehavior<InsertCommand<Order>, Order>>.Instance);

        var executions = 0;
        await solo.Handle(command, () => { executions++; return Task.FromResult(command.Entity); }, CancellationToken.None);

        Assert.Equal(1, executions);
    }
}
