using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundry.Core.Attributes;
using Foundry.Core.Entities;
using Foundry.Mongo.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace Foundry.Mongo.Tests;

/// <summary>
/// The archival sweep's non-transactional path, against a real standalone MongoDB.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="DataArchivalWorker"/> has two ways to move a year of documents: one transaction, or
/// copy-verify-delete. Which one runs is decided by asking the server, and MongoDB offers
/// transactions only on a replica set — so exactly one of the two branches can execute in any given
/// deployment, and whichever the infrastructure does not provide is covered by reading it.
/// </para>
/// <para>
/// That was the transactional branch for as long as this project's docker-compose and CI ran a
/// standalone. Both now run a single-node replica set, which fixed that branch and immediately made
/// the fallback the unreachable one — the two swapped places rather than the gap closing. This suite
/// closes it from the other side by connecting to a standalone, so both branches run on every CI
/// run and the fallback is exercised on the deployment shape a developer is most likely to have.
/// </para>
/// <para>
/// The fallback is not a lesser path. It is what stands between an interrupted sweep and permanent
/// data loss: it inserts into the archive, confirms every document arrived, and only then deletes
/// from the active collection. Each of those three steps is asserted here separately, because the
/// order is the entire safety property and a sweep that skipped the verification step would still
/// pass a test that only checked the happy path.
/// </para>
/// </remarks>
public class ArchivalFallbackTests : IDisposable
{
    /// <summary>A standalone mongod, separate from the replica set the rest of the suite uses.</summary>
    private const string ConnectionString = "mongodb://localhost:27018";

    private readonly string _dbName = $"FoundryMongo_Fallback_{Guid.NewGuid():N}";
    private readonly MongoClient _client = new(Settings());
    private readonly IMongoDatabase _db;

    /// <summary>
    /// The driver's default is to spend 30 seconds looking for a server before giving up, per test.
    /// Ten seconds is far longer than a local or CI mongod needs and turns "the standalone is not
    /// running" into a five-minute suite that ends in a timeout rather than in the message below.
    /// </summary>
    private static MongoClientSettings Settings()
    {
        var settings = MongoClientSettings.FromConnectionString(ConnectionString);
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(10);
        return settings;
    }

    /// <summary>Archived after one year, so a three-year-old record is unambiguously cold.</summary>
    [Partitioned(1)]
    public record Invoice : BaseEntity<ObjectId>
    {
        public string Reference { get; set; } = string.Empty;
    }

    public ArchivalFallbackTests()
    {
        Foundry.Mongo.Infrastructure.Conventions.MongoDbConventions.Register();
        _db = _client.GetDatabase(_dbName);
    }

    /// <summary>Set once the server has been confirmed usable, so cleanup does not wait on a dead one.</summary>
    private bool _serverUsable;

    public void Dispose()
    {
        if (!_serverUsable) return;
        try { _client.DropDatabase(_dbName); } catch { /* cleanup is best effort */ }
    }

    private static string Plural => "Invoices";

    private static string ArchiveFor(ObjectId id) => $"{Plural}_{id.CreationTime.Year}";

    private static ObjectId AgedId(int yearsAgo) =>
        ObjectId.GenerateNewId(DateTime.UtcNow.AddYears(-yearsAgo));

    private async Task SeedAsync(string collection, ObjectId id, string reference) =>
        await _db.GetCollection<BsonDocument>(collection).InsertOneAsync(new BsonDocument
        {
            ["_id"] = id,
            ["reference"] = reference,
            ["createdAtUtc"] = DateTime.UtcNow.AddYears(-3),
            ["updatedAtUtc"] = DateTime.UtcNow.AddYears(-3),
        });

    private async Task<long> CountAsync(string collection, params ObjectId[] ids) =>
        await _db.GetCollection<BsonDocument>(collection)
            .CountDocumentsAsync(Builders<BsonDocument>.Filter.In("_id", ids));

    /// <summary>Captures what the worker logged, so a warning can be asserted rather than assumed.</summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

    private CapturingLogger<DataArchivalWorker> _log = new();

    private DataArchivalWorker Worker()
    {
        _log = new CapturingLogger<DataArchivalWorker>();
        return new DataArchivalWorker(new ServiceCollection().BuildServiceProvider(), _db, _log);
    }

    /// <summary>
    /// What is wrong with the server on 27018, or <c>null</c> when it is the standalone wanted.
    /// </summary>
    /// <remarks>
    /// Probed once for the whole class. xUnit builds a fresh instance per test, and an absent server
    /// costs a full server-selection timeout to discover — paid ten times over, that turns a missing
    /// container into a three-minute wait for a message the first test already knew.
    /// </remarks>
    private static readonly Lazy<Task<string?>> ServerProblem = new(ProbeAsync);

    private static async Task<string?> ProbeAsync()
    {
        BsonDocument hello;
        try
        {
            hello = await new MongoClient(Settings()).GetDatabase("admin")
                .RunCommandAsync<BsonDocument>(new BsonDocument("hello", 1));
        }
        catch (Exception ex)
        {
            return "These tests cover the archival sweep's non-transactional path, which requires a "
                + $"standalone mongod on {ConnectionString}. Start one with "
                + "'docker compose up -d mongodb-standalone'. " + ex.Message;
        }

        if (!hello.Contains("setName") && hello.GetValue("msg", "").AsString != "isdbgrid") return null;

        return $"The server on {ConnectionString} is a replica set. This suite covers the "
            + "copy-verify-delete fallback, which a replica set never selects, so every test here "
            + "would pass by exercising the transactional path instead.";
    }

    /// <summary>
    /// Fails, rather than skips, when the server is not a standalone.
    /// </summary>
    /// <remarks>
    /// A skip would let this suite report success while testing the transactional path a second
    /// time — which is precisely the failure it exists to prevent. Same reason the replica-set tests
    /// in <see cref="PartitioningTests"/> assert their infrastructure instead of detecting it.
    /// </remarks>
    private async Task RequireStandaloneAsync()
    {
        var problem = await ServerProblem.Value;
        if (problem is not null) Assert.Fail(problem);

        _serverUsable = true;
    }

    // ── The branch selection ────────────────────────────────────────────────

    [Fact]
    public async Task AStandaloneServerSelectsTheFallbackAndSaysSo()
    {
        // The warning is the only signal an operator gets that their deployment cannot archive
        // atomically. Asserted because a silent fallback is indistinguishable from a transaction.
        await RequireStandaloneAsync();

        var id = AgedId(3);
        await SeedAsync(Plural, id, "FALLBACK-1");

        await Worker().RunSweepAsync(CancellationToken.None);

        Assert.Contains(_log.Entries, e =>
            e.Level == LogLevel.Warning && e.Message.Contains("standalone", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(_log.Entries, e => e.Message.Contains("transactionally"));
    }

    // ── Copy, verify, delete ────────────────────────────────────────────────

    [Fact]
    public async Task TheFallbackMovesAnAgedRecordIntoItsYearArchive()
    {
        await RequireStandaloneAsync();

        var id = AgedId(3);
        await SeedAsync(Plural, id, "MOVE-ME");

        await Worker().RunSweepAsync(CancellationToken.None);

        Assert.Equal(1, await CountAsync(ArchiveFor(id), id));
        Assert.Equal(0, await CountAsync(Plural, id));
    }

    [Fact]
    public async Task TheArchivedDocumentKeepsItsContent()
    {
        // A move that loses fields is a move that lost the data, and the count assertions above
        // cannot tell the difference.
        await RequireStandaloneAsync();

        var id = AgedId(3);
        await SeedAsync(Plural, id, "CONTENT");

        await Worker().RunSweepAsync(CancellationToken.None);

        var archived = await _db.GetCollection<BsonDocument>(ArchiveFor(id))
            .Find(Builders<BsonDocument>.Filter.Eq("_id", id)).SingleAsync();

        Assert.Equal("CONTENT", archived["reference"].AsString);
    }

    [Fact]
    public async Task EachYearGoesToItsOwnArchive()
    {
        await RequireStandaloneAsync();

        var ids = new[] { AgedId(3), AgedId(4), AgedId(5) };
        foreach (var id in ids) await SeedAsync(Plural, id, $"YEAR-{id.CreationTime.Year}");

        await Worker().RunSweepAsync(CancellationToken.None);

        foreach (var id in ids)
        {
            Assert.Equal(1, await CountAsync(ArchiveFor(id), id));
            Assert.Equal(0, await CountAsync(Plural, id));
        }
    }

    [Fact]
    public async Task RecentRecordsAreLeftAlone()
    {
        await RequireStandaloneAsync();

        var recent = ObjectId.GenerateNewId();
        await SeedAsync(Plural, recent, "STAY-HOT");

        await Worker().RunSweepAsync(CancellationToken.None);

        Assert.Equal(1, await CountAsync(Plural, recent));
    }

    // ── Interruption and re-runs ────────────────────────────────────────────

    [Fact]
    public async Task ASweepInterruptedAfterTheCopyIsCorrectedByARerun()
    {
        // The window the fallback trades away for being able to run at all: without a transaction a
        // crash between insert and delete leaves the document in both collections. That state is
        // recoverable only if the next sweep tolerates the copy already being there, so this is the
        // difference between a fallback and a permanently wedged one.
        await RequireStandaloneAsync();

        var id = AgedId(3);
        await SeedAsync(Plural, id, "INTERRUPTED");
        await SeedAsync(ArchiveFor(id), id, "INTERRUPTED");

        await Worker().RunSweepAsync(CancellationToken.None);

        Assert.Equal(1, await CountAsync(ArchiveFor(id), id));
        Assert.Equal(0, await CountAsync(Plural, id));
    }

    [Fact]
    public async Task OneAlreadyCopiedDocumentDoesNotStrandTheRest()
    {
        // Unordered inserts, so the duplicate does not abort the batch. Ordered ones would leave
        // every document after the first duplicate in the active collection, and the sweep would
        // report success.
        await RequireStandaloneAsync();

        var already = AgedId(3);
        var fresh = new[] { AgedId(3), AgedId(3) };
        await SeedAsync(Plural, already, "ALREADY");
        await SeedAsync(ArchiveFor(already), already, "ALREADY");
        foreach (var id in fresh) await SeedAsync(Plural, id, "PENDING");

        await Worker().RunSweepAsync(CancellationToken.None);

        Assert.Equal(0, await CountAsync(Plural, [already, .. fresh]));
        Assert.Equal(3, await CountAsync(ArchiveFor(already), [already, .. fresh]));
    }

    // ── The verification step ───────────────────────────────────────────────

    [Fact]
    public async Task NothingIsDeletedWhenTheCopyDidNotFullyLand()
    {
        // The step the whole fallback rests on. A unique index on the archive rejects the second
        // document, and its write error is a duplicate key -- the same category the sweep treats as
        // "already archived" and continues past. Only the count check afterwards can tell the two
        // apart, so if it were removed this sweep would delete a document that was never copied.
        await RequireStandaloneAsync();

        var ids = new[] { AgedId(3), AgedId(3) };
        var archive = ArchiveFor(ids[0]);
        await _db.GetCollection<BsonDocument>(archive).Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("reference"),
                new CreateIndexOptions { Unique = true }));

        foreach (var id in ids) await SeedAsync(Plural, id, "COLLIDES");

        await Assert.ThrowsAsync<AggregateException>(
            () => Worker().RunSweepAsync(CancellationToken.None));

        // Both still in the active collection: the partial copy was refused rather than half-moved.
        Assert.Equal(2, await CountAsync(Plural, ids));
        Assert.Equal(1, await CountAsync(archive, ids));
    }

    [Fact]
    public async Task AFailedSweepNamesTheCollectionAndTheShortfall()
    {
        // An operator reads this message at 3am with the data still safe in the active collection.
        // It has to say which collection, how far it got, and that a re-run is the fix.
        await RequireStandaloneAsync();

        var ids = new[] { AgedId(3), AgedId(3) };
        var archive = ArchiveFor(ids[0]);
        await _db.GetCollection<BsonDocument>(archive).Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("reference"),
                new CreateIndexOptions { Unique = true }));

        foreach (var id in ids) await SeedAsync(Plural, id, "COLLIDES");

        var error = await Assert.ThrowsAsync<AggregateException>(
            () => Worker().RunSweepAsync(CancellationToken.None));

        var inner = Assert.IsType<InvalidOperationException>(error.InnerExceptions.Single());
        Assert.Contains(archive, inner.Message);
        Assert.Contains("1 of 2", inner.Message);
        Assert.Contains("re-run", inner.Message);
    }

    [Fact]
    public async Task ASweepThatFailsOnOneYearStillArchivesTheOthers()
    {
        // One bad year must not hold the rest hostage, and must still be reported -- the same rule
        // the sweep already applies across entity types, applied within one.
        //
        // The blocked year is deliberately the *older* one. Documents are selected by a filter on
        // _id, so they come back in index order, oldest first: putting the failure second makes this
        // test pass whether or not the years are independent, which is the same as not testing it.
        await RequireStandaloneAsync();

        var blocked = new[] { AgedId(5), AgedId(5) };
        var fine = AgedId(3);
        var blockedArchive = ArchiveFor(blocked[0]);

        await _db.GetCollection<BsonDocument>(blockedArchive).Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("reference"),
                new CreateIndexOptions { Unique = true }));

        foreach (var id in blocked) await SeedAsync(Plural, id, "COLLIDES");
        await SeedAsync(Plural, fine, "UNAFFECTED");

        await Assert.ThrowsAsync<AggregateException>(
            () => Worker().RunSweepAsync(CancellationToken.None));

        Assert.Equal(1, await CountAsync(ArchiveFor(fine), fine));
        Assert.Equal(0, await CountAsync(Plural, fine));
        Assert.Equal(2, await CountAsync(Plural, blocked));
    }

    // ── Batching ────────────────────────────────────────────────────────────

    [Fact]
    public async Task AllDocumentsAreArchivedAcrossMultipleBatches()
    {
        // Verifies that the batched archival loop correctly processes documents across multiple
        // batch iterations without stranding or double-archiving any. Seeds more documents than
        // a single batch to ensure multiple iterations of the batching loop execute.
        await RequireStandaloneAsync();

        var batchSize = DataArchivalWorker.ArchivalBatchSize;
        var documentCount = batchSize * 2 + 5; // Seed 2.5x the batch size
        var ids = new List<ObjectId>();

        // Seed all documents old enough to archive, spread across the batch to ensure
        // grouping by year happens correctly across batch boundaries
        for (int i = 0; i < documentCount; i++)
        {
            var id = AgedId(3);
            ids.Add(id);
            await SeedAsync(Plural, id, $"BATCH-{i}");
        }

        await Worker().RunSweepAsync(CancellationToken.None);

        // Verify all documents are in the archive and none in the active collection
        Assert.Equal(0, await CountAsync(Plural, ids.ToArray()));
        Assert.Equal(documentCount, await CountAsync(ArchiveFor(ids[0]), ids.ToArray()));
    }
}
