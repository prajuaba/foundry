using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundry.Core.Outbox;
using Foundry.Mongo.DependencyInjection;
using Foundry.Mongo.Repositories;
using Foundry.Mongo.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace Foundry.Mongo.Tests;

/// <summary>
/// The outbox when publishing fails, which is the case it exists for.
/// </summary>
/// <remarks>
/// <para>
/// It was proven for one message against a healthy broker and nothing else. Under failure it did not
/// work: the worker polls every two seconds, retried on that interval with no delay, and selected
/// messages with <c>RetryCount &lt; 5</c>. <strong>A broker outage of ten seconds therefore exhausted
/// every pending message</strong> — and exhaustion was not an event, it was the absence of one. The
/// fifth failure simply stopped matching the query, so the rows sat in the collection unpublished,
/// unmarked, and unmentioned, while an operator watching the queue drain saw what success looks like.
/// </para>
/// <para>
/// These drive the worker's own loop rather than reimplementing it, and use a dispatcher that fails
/// on demand rather than a real broker, so the retry semantics are what is under test rather than
/// Kafka's.
/// </para>
/// </remarks>
public class OutboxUnderFailureTests : IDisposable
{
    private readonly string _dbName;
    private readonly MongoClient _client;
    private readonly ServiceProvider _services;

    /// <summary>A dispatcher whose availability the test controls.</summary>
    private sealed class ControllableDispatcher : IOutboxDispatcher
    {
        public bool Available { get; set; }
        public List<string> Published { get; } = [];

        public Task DispatchAsync(string eventType, string payload, string? correlationId = null,
            string? traceParent = null, string? topic = null, CancellationToken ct = default)
        {
            if (!Available) throw new InvalidOperationException("broker unavailable");

            Published.Add(payload);
            return Task.CompletedTask;
        }
    }

    private readonly ControllableDispatcher _dispatcher = new();

    public OutboxUnderFailureTests()
    {
        Foundry.Mongo.Infrastructure.Conventions.MongoDbConventions.Register();
        _dbName = $"FoundryMongo_OutboxFailure_{Guid.NewGuid():N}";
        _client = new MongoClient("mongodb://localhost:27017");

        var services = new ServiceCollection();
        services.AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        services.AddFoundryMongo(options =>
        {
            options.ConnectionString = "mongodb://localhost:27017";
            options.DatabaseName = _dbName;
        });
        services.AddSingleton<IOutboxDispatcher>(_dispatcher);

        _services = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _services.Dispose();
        try { _client.DropDatabase(_dbName); } catch { /* cleanup is best effort */ }
    }

    private IRepository<OutboxMessage> Repository()
        => _services.GetRequiredService<IRepository<OutboxMessage>>();

    private async Task<ObjectId> EnqueueAsync(string reference)
    {
        var message = new OutboxMessage
        {
            Id = ObjectId.GenerateNewId(),
            EventType = "Test.OrderPlaced, Test",
            Payload = $$"""{"reference":"{{reference}}"}""",
            CreatedAt = DateTime.UtcNow
        };

        await Repository().InsertAsync(message);
        return message.Id;
    }

    /// <summary>Runs exactly one sweep of the worker's own loop.</summary>
    private async Task SweepAsync()
    {
        var worker = new OutboxPublisherWorker(_services, NullLogger<OutboxPublisherWorker>.Instance);
        var method = typeof(OutboxPublisherWorker).GetMethod(
            "ProcessOutboxMessagesAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        await (Task)method.Invoke(worker, [CancellationToken.None])!;
    }

    private async Task<OutboxMessage> ReadAsync(ObjectId id) => (await Repository().GetByIdAsync(id))!;

    /// <summary>Makes a message due now, standing in for the passage of the backoff delay.</summary>
    private async Task MakeDueAsync(ObjectId id)
    {
        var message = await ReadAsync(id);
        message.NextAttemptAt = DateTime.UtcNow.AddSeconds(-1);
        await Repository().UpdateAsync(message);
    }

    // ── A failed publish is retried, not lost ───────────────────────────────

    [Fact]
    public async Task AFailedPublishLeavesTheMessageUnprocessed()
    {
        _dispatcher.Available = false;
        var id = await EnqueueAsync("REF-1");

        await SweepAsync();

        var message = await ReadAsync(id);
        Assert.Null(message.ProcessedAt);
        Assert.Equal(1, message.RetryCount);
        Assert.Contains("broker unavailable", message.ErrorMessage);
        Assert.Null(message.DeadLetteredAt);
    }

    [Fact]
    public async Task AMessageSurvivesAnOutageAndPublishesWhenTheBrokerReturns()
    {
        // The whole point of an outbox. Before backoff this failed for any outage longer than ten
        // seconds, because five attempts two seconds apart is all it took to exhaust one.
        _dispatcher.Available = false;
        var id = await EnqueueAsync("REF-2");

        await SweepAsync();
        await MakeDueAsync(id);
        await SweepAsync();

        _dispatcher.Available = true;
        await MakeDueAsync(id);
        await SweepAsync();

        var message = await ReadAsync(id);
        Assert.NotNull(message.ProcessedAt);
        Assert.Null(message.DeadLetteredAt);
        Assert.Contains("REF-2", _dispatcher.Published.Single());
    }

    // ── Backoff ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task AFailedMessageIsNotRetriedOnTheNextSweep()
    {
        // Retries ran on the two-second polling interval, so the attempts were spent almost as fast
        // as they could be counted.
        _dispatcher.Available = false;
        var id = await EnqueueAsync("REF-3");

        await SweepAsync();
        await SweepAsync();
        await SweepAsync();

        Assert.Equal(1, (await ReadAsync(id)).RetryCount);
    }

    [Fact]
    public async Task EachFailureBacksOffFurtherThanTheLast()
    {
        _dispatcher.Available = false;
        var id = await EnqueueAsync("REF-4");

        await SweepAsync();
        var first = (await ReadAsync(id)).NextAttemptAt!.Value;

        await MakeDueAsync(id);
        await SweepAsync();
        var second = (await ReadAsync(id)).NextAttemptAt!.Value;

        Assert.True(second - DateTime.UtcNow > first - DateTime.UtcNow,
            "the second delay should exceed the first");
    }

    // ── Exhaustion is an event, not a silence ───────────────────────────────

    [Fact]
    public async Task AMessageThatExhaustsItsAttemptsIsMarkedRatherThanForgotten()
    {
        _dispatcher.Available = false;
        var id = await EnqueueAsync("REF-5");

        for (var attempt = 0; attempt < 5; attempt++)
        {
            await MakeDueAsync(id);
            await SweepAsync();
        }

        var message = await ReadAsync(id);
        Assert.Equal(5, message.RetryCount);
        Assert.NotNull(message.DeadLetteredAt);
        Assert.Null(message.ProcessedAt);
    }

    [Fact]
    public async Task AnAbandonedMessageIsNotPublishedIfTheBrokerReturns()
    {
        // Deliberate, and the reason abandonment is recorded rather than implied: republishing an
        // arbitrarily old message without someone deciding to is its own hazard. Clearing
        // DeadLetteredAt is that decision.
        _dispatcher.Available = false;
        var id = await EnqueueAsync("REF-6");

        for (var attempt = 0; attempt < 5; attempt++)
        {
            await MakeDueAsync(id);
            await SweepAsync();
        }

        _dispatcher.Available = true;
        await MakeDueAsync(id);
        await SweepAsync();

        Assert.Empty(_dispatcher.Published);
        Assert.NotNull((await ReadAsync(id)).DeadLetteredAt);
    }

    [Fact]
    public async Task AnAbandonedMessageIsDistinguishableFromAPublishedOne()
    {
        // The failure this replaces: an exhausted message stopped matching the worker's query, so
        // the only difference between "published" and "given up on" was a RetryCount nobody reads.
        _dispatcher.Available = false;
        var abandoned = await EnqueueAsync("REF-7");

        for (var attempt = 0; attempt < 5; attempt++)
        {
            await MakeDueAsync(abandoned);
            await SweepAsync();
        }

        _dispatcher.Available = true;
        var published = await EnqueueAsync("REF-8");
        await SweepAsync();

        var stuck = await ReadAsync(abandoned);
        var sent = await ReadAsync(published);

        Assert.NotNull(stuck.DeadLetteredAt);
        Assert.Null(stuck.ProcessedAt);
        Assert.Null(sent.DeadLetteredAt);
        Assert.NotNull(sent.ProcessedAt);
    }

    // ── Ordering ────────────────────────────────────────────────────────────

    [Fact]
    public async Task MessagesArePublishedOldestFirst()
    {
        _dispatcher.Available = true;

        await EnqueueAsync("FIRST");
        await EnqueueAsync("SECOND");
        await EnqueueAsync("THIRD");

        await SweepAsync();

        Assert.Equal(3, _dispatcher.Published.Count);
        Assert.Contains("FIRST", _dispatcher.Published[0]);
        Assert.Contains("SECOND", _dispatcher.Published[1]);
        Assert.Contains("THIRD", _dispatcher.Published[2]);
    }

    [Fact]
    public async Task AFailingMessageDoesNotBlockTheOnesBehindIt()
    {
        // A head-of-line block would be the other way to get this wrong: one poisoned message
        // stalling the queue is as total an outage as losing them.
        _dispatcher.Available = false;
        var stuck = await EnqueueAsync("STUCK");
        await SweepAsync();

        _dispatcher.Available = true;
        await EnqueueAsync("BEHIND");
        await SweepAsync();

        Assert.Contains("BEHIND", _dispatcher.Published.Single());
        Assert.Null((await ReadAsync(stuck)).ProcessedAt);
    }
}
