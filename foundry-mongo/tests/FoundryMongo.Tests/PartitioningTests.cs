using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundry.Core.Attributes;
using Foundry.Core.Audit;
using Foundry.Core.Paging;
using Foundry.Core.Entities;
using Foundry.Core.Tenant;
using Foundry.Mongo.Repositories;
using Foundry.Mongo.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace Foundry.Mongo.Tests;

/// <summary>
/// Hot/cold partitioning, against a real MongoDB.
/// </summary>
/// <remarks>
/// <para>
/// Named in this project's own assessment as senior-level work in the data layer, and it had
/// <em>no tests at all</em> — neither <see cref="PartitionedRepository{T}"/> nor
/// <see cref="DataArchivalWorker"/> was executed by anything. It was checked here because the
/// pattern that held five times over — a feature that reads correctly and has never run — predicted
/// that it would not work, and predicting where to look is the only use that pattern has.
/// </para>
/// <para>
/// Routing is by <c>ObjectId.CreationTime</c>, so a record's age is expressed by generating its id
/// with a timestamp rather than by waiting.
/// </para>
/// </remarks>
public class PartitioningTests : IDisposable
{
    private const string ConnectionString = "mongodb://localhost:27017";

    private readonly string _dbName = $"FoundryMongo_Partition_{Guid.NewGuid():N}";
    private readonly MongoClient _client = new(ConnectionString);
    private readonly IMongoDatabase _db;

    /// <summary>Archived after one year, so a two-year-old record is unambiguously cold.</summary>
    [Partitioned(1)]
    public record Ledger : BaseEntity<ObjectId>, IVersionable, IMultiTenant
    {
        public string TenantId { get; set; } = string.Empty;
        public string Reference { get; set; } = string.Empty;
    }

    private sealed class FixedTenant(string? tenantId) : ITenantContext
    {
        public string? TenantId { get; private set; } = tenantId;
        public bool HasTenant => !string.IsNullOrWhiteSpace(TenantId);
        public void SetTenantId(string tenantId) => TenantId = tenantId;
    }

    public PartitioningTests()
    {
        Foundry.Mongo.Infrastructure.Conventions.MongoDbConventions.Register();
        _db = _client.GetDatabase(_dbName);
    }

    public void Dispose()
    {
        try { _client.DropDatabase(_dbName); } catch { /* cleanup is best effort */ }
    }

    private PartitionedRepository<Ledger> RepoFor(string? tenant) =>
        new(_db, tenantContext: new FixedTenant(tenant));

    /// <summary>An id whose creation time is <paramref name="yearsAgo"/> in the past.</summary>
    private static ObjectId AgedId(int yearsAgo) =>
        ObjectId.GenerateNewId(DateTime.UtcNow.AddYears(-yearsAgo));

    private static string Plural => "Ledgers";

    /// <summary>Writes straight into a collection, bypassing the repository's routing.</summary>
    private async Task SeedRawAsync(string collection, ObjectId id, string tenant, string reference)
    {
        await _db.GetCollection<BsonDocument>(collection).InsertOneAsync(new BsonDocument
        {
            ["_id"] = id,
            ["tenantId"] = tenant,
            ["reference"] = reference,
            ["version"] = 1,
            ["createdAtUtc"] = DateTime.UtcNow.AddYears(-3),
            ["updatedAtUtc"] = DateTime.UtcNow.AddYears(-3),
        });
    }

    // ── Routing ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task ARecentRecordIsWrittenToAndReadFromTheActiveCollection()
    {
        var repo = RepoFor("acme");
        var ledger = new Ledger { Id = ObjectId.GenerateNewId(), Reference = "HOT-1" };

        await repo.InsertAsync(ledger);

        Assert.Equal("HOT-1", (await repo.GetByIdAsync(ledger.Id))!.Reference);
        Assert.Equal(1, await _db.GetCollection<BsonDocument>(Plural)
            .CountDocumentsAsync(Builders<BsonDocument>.Filter.Eq("_id", ledger.Id)));
    }

    [Fact]
    public async Task AnAgedRecordIsReadFromItsYearArchive()
    {
        // The routing claim: an id older than the threshold resolves to Ledgers_{year}.
        var oldId = AgedId(3);
        await SeedRawAsync($"{Plural}_{oldId.CreationTime.Year}", oldId, "acme", "COLD-1");

        var found = await RepoFor("acme").GetByIdAsync(oldId);

        Assert.NotNull(found);
        Assert.Equal("COLD-1", found!.Reference);
    }

    [Fact]
    public async Task AnAgedRecordNotYetArchivedIsStillFoundInTheActiveCollection()
    {
        // A row past the threshold stays in the active collection until the archival sweep moves
        // it. Reads routed by id age alone and never fell back, so it was unreachable -- by id,
        // for update and for delete -- for as long as the sweep had not run.
        var oldId = AgedId(3);
        await SeedRawAsync(Plural, oldId, "acme", "STRANDED");
        var repo = RepoFor("acme");

        var found = await repo.GetByIdAsync(oldId);

        Assert.Equal("STRANDED", found!.Reference);
        found.Reference = "STRANDED-EDITED";
        await repo.UpdateAsync(found);
        Assert.Equal("STRANDED-EDITED", (await repo.GetByIdAsync(oldId))!.Reference);
    }

    // ── Reads span every partition ──────────────────────────────────────────
    //
    // Rows are archived by the year in their id. Reads chose partitions from date comparisons on
    // CreatedAtUtc in the filter, and read only the active collection for every other filter -- so
    // a report filtering on a period, or on nothing, silently left out every archived row.

    private async Task<(ObjectId Cold, ObjectId Hot)> OneColdOneHotAsync(string tenant = "acme")
    {
        var cold = AgedId(3);
        await SeedRawAsync($"{Plural}_{cold.CreationTime.Year}", cold, tenant, "R-COLD");
        var hot = ObjectId.GenerateNewId();
        await RepoFor(tenant).InsertAsync(new Ledger { Id = hot, Reference = "R-HOT" });
        return (cold, hot);
    }

    [Fact]
    public async Task AFilterOnAnyFieldReadsTheArchives()
    {
        await OneColdOneHotAsync();
        var repo = RepoFor("acme");

        var rows = await repo.FindManyAsync(r => r.Reference.StartsWith("R-"));

        Assert.Equal(["R-COLD", "R-HOT"], rows.Select(r => r.Reference).Order());
        Assert.Equal(2, await repo.CountAsync(r => r.Reference.StartsWith("R-")));
        Assert.Equal(2, await repo.CountAsync());
    }

    [Fact]
    public async Task ASortedLimitedReadIsInOrderAcrossPartitions()
    {
        await OneColdOneHotAsync();
        var repo = RepoFor("acme");

        var first = await repo.FindManyAsync(sortBy: "Reference", sortOrder: SortOrder.Ascending, limit: 1);
        var last = await repo.FindManyAsync(sortBy: "Reference", sortOrder: SortOrder.Descending, limit: 1);

        Assert.Equal("R-COLD", Assert.Single(first).Reference);
        Assert.Equal("R-HOT", Assert.Single(last).Reference);
    }

    [Fact]
    public async Task OffsetPagesAreOfTheWholeNotOfEachPartition()
    {
        // Page 2 used to be page 2 of each partition, concatenated: here, nothing at all.
        await OneColdOneHotAsync();
        var repo = RepoFor("acme");
        var sort = new SortRequest { FieldName = "Reference", Order = SortOrder.Ascending };

        var page1 = await repo.GetPagedAsync(new PagedRequest { PageNumber = 1, PageSize = 1, SortBy = sort });
        var page2 = await repo.GetPagedAsync(new PagedRequest { PageNumber = 2, PageSize = 1, SortBy = sort });

        Assert.Equal("R-COLD", Assert.Single(page1.Items).Reference);
        Assert.Equal("R-HOT", Assert.Single(page2.Items).Reference);
        Assert.Equal(2, page2.TotalRecords);
    }

    [Fact]
    public async Task AComposedQueryReadsTheArchivesWithinTheTenant()
    {
        await OneColdOneHotAsync("acme");
        await OneColdOneHotAsync("globex");

        var acme = RepoFor("acme").Query().Where(r => r.Reference.StartsWith("R-")).ToList();

        Assert.Equal(2, acme.Count);
        Assert.All(acme, r => Assert.Equal("acme", r.TenantId));
    }

    [Fact]
    public async Task AnAggregationIsTenantScopedInEveryPartition()
    {
        // The archives were unioned in unfiltered and the active collection was not filtered at
        // all, so a pipeline ran over every tenant's rows.
        await OneColdOneHotAsync("acme");
        await OneColdOneHotAsync("globex");
        var pipeline = PipelineDefinition<Ledger, Ledger>.Create(new BsonDocument("$match", new BsonDocument()));

        var rows = await RepoFor("acme").AggregateAsync(pipeline);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal("acme", r.TenantId));
    }

    // ── Restoring a soft delete ─────────────────────────────────────────────

    [Fact]
    public async Task ADeletedRowIsRestoredOnlyForItsOwnTenant_AndAsStored()
    {
        // It was found by id alone, so any tenant could restore another's row; and it went back
        // through the encrypting insert, stamped as new.
        await RequireReplicaSetAsync();
        var sink = new RecordingSink();
        var acme = new PartitionedRepository<SoftLedger>(_db, auditSink: sink, tenantContext: new FixedTenant("acme"));
        var globex = new PartitionedRepository<SoftLedger>(_db, tenantContext: new FixedTenant("globex"));
        var ledger = new SoftLedger { Id = ObjectId.GenerateNewId(), TenantId = "acme", Reference = "BACK" };
        await acme.InsertAsync(ledger);
        var created = DateTime.UtcNow.AddDays(-30);
        await _db.GetCollection<BsonDocument>("SoftLedgers").UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", ledger.Id),
            Builders<BsonDocument>.Update.Set("createdAtUtc", created));
        await acme.DeleteByObjectIdAsync(ledger.Id, "operator-1");
        sink.Written.Clear();

        await globex.RestoreDeletedAsync(ledger.Id);
        Assert.Null(await acme.GetByIdAsync(ledger.Id));

        await acme.RestoreDeletedAsync(ledger.Id);

        var restored = await _db.GetCollection<BsonDocument>("SoftLedgers")
            .Find(Builders<BsonDocument>.Filter.Eq("_id", ledger.Id)).SingleAsync();
        Assert.False(restored["isDeleted"].AsBoolean);
        Assert.Equal(created, restored["createdAtUtc"].ToUniversalTime(), TimeSpan.FromSeconds(1));
        Assert.Equal(3, restored["version"].AsInt32);
        Assert.Equal(AuditAction.Restored, Assert.Single(sink.Written).Action);
        Assert.Equal("BACK", (await acme.GetByIdAsync(ledger.Id))!.Reference);
    }

    // ── Tenant isolation across the partition boundary ──────────────────────

    [Fact]
    public async Task AnArchivedRecordIsNotReadableByAnotherTenant()
    {
        // The whole point. Tenant isolation must not depend on how old a record is.
        var oldId = AgedId(3);
        await SeedRawAsync($"{Plural}_{oldId.CreationTime.Year}", oldId, "acme", "COLD-ACME");

        Assert.Null(await RepoFor("globex").GetByIdAsync(oldId));
        Assert.NotNull(await RepoFor("acme").GetByIdAsync(oldId));
    }

    [Fact]
    public async Task AnArchivedRecordIsNotListedForAnotherTenant()
    {
        var oldId = AgedId(3);
        await SeedRawAsync($"{Plural}_{oldId.CreationTime.Year}", oldId, "acme", "COLD-ACME");

        var rows = await RepoFor("globex").FindManyAsync();

        Assert.DoesNotContain(rows, r => r.Reference == "COLD-ACME");
    }

    [Fact]
    public async Task AnActiveRecordIsStillTenantScoped()
    {
        // The control: isolation on the hot path already worked, so a failure above is about the
        // archive specifically and not about tenancy in general.
        var ledger = new Ledger { Id = ObjectId.GenerateNewId(), Reference = "HOT-ACME" };
        await RepoFor("acme").InsertAsync(ledger);

        Assert.Null(await RepoFor("globex").GetByIdAsync(ledger.Id));
    }

    // ── The archival sweep ──────────────────────────────────────────────────

    [Fact]
    public async Task TheArchivalWorkerMovesAnAgedRecordOutOfTheActiveCollection()
    {
        // The sweep is what puts records where the routing above expects to find them. Without it,
        // a record ages past the threshold, stays in the active collection, and stops being
        // readable -- see AnAgedRecordIsNotLookedForInTheActiveCollection.
        var oldId = AgedId(3);
        await SeedRawAsync(Plural, oldId, "acme", "TO-ARCHIVE");

        var services = new ServiceCollection().BuildServiceProvider();
        var worker = new DataArchivalWorker(services, _db, NullLogger<DataArchivalWorker>.Instance);

        await worker.RunSweepAsync(CancellationToken.None);

        var archived = await _db.GetCollection<BsonDocument>($"{Plural}_{oldId.CreationTime.Year}")
            .CountDocumentsAsync(Builders<BsonDocument>.Filter.Eq("_id", oldId));
        var remaining = await _db.GetCollection<BsonDocument>(Plural)
            .CountDocumentsAsync(Builders<BsonDocument>.Filter.Eq("_id", oldId));

        Assert.Equal(1, archived);
        Assert.Equal(0, remaining);
    }

    [Fact]
    public async Task TheArchivalWorkerLeavesRecentRecordsAlone()
    {
        var recent = new Ledger { Id = ObjectId.GenerateNewId(), Reference = "STAY-HOT" };
        await RepoFor("acme").InsertAsync(recent);

        var services = new ServiceCollection().BuildServiceProvider();
        var worker = new DataArchivalWorker(services, _db, NullLogger<DataArchivalWorker>.Instance);

        await worker.RunSweepAsync(CancellationToken.None);

        Assert.Equal(1, await _db.GetCollection<BsonDocument>(Plural)
            .CountDocumentsAsync(Builders<BsonDocument>.Filter.Eq("_id", recent.Id)));
    }

    [Fact]
    public async Task AFailedSweepIsReportedRatherThanSwallowed()
    {
        // The sweep ran inside a multi-document transaction, which a standalone mongod does not
        // support -- and every exception was caught and logged. On the framework's own default
        // infrastructure archival therefore never happened, and nothing said so.
        var oldId = AgedId(3);
        await SeedRawAsync(Plural, oldId, "acme", "TO-ARCHIVE");

        var services = new ServiceCollection().BuildServiceProvider();
        var worker = new DataArchivalWorker(services, _db, NullLogger<DataArchivalWorker>.Instance);

        // Whatever the deployment supports, the sweep either archives the record or says why. It
        // must not report success having done nothing.
        await worker.RunSweepAsync(CancellationToken.None);

        var remaining = await _db.GetCollection<BsonDocument>(Plural)
            .CountDocumentsAsync(Builders<BsonDocument>.Filter.Eq("_id", oldId));

        Assert.Equal(0, remaining);
    }

    // ── Audit ───────────────────────────────────────────────────────────────

    [Partitioned(1)]
    public record SoftLedger : BaseEntity<ObjectId>, IMultiTenant, ISoftDelete
    {
        public string TenantId { get; set; } = string.Empty;
        public string Reference { get; set; } = string.Empty;
        public bool IsDeleted { get; init; }
        public DateTime? DeletedAt { get; init; }
    }

    private sealed class RecordingSink : IAuditSink
    {
        public readonly List<AuditLogEntry> Written = new();

        public Task WriteAsync(AuditLogEntry entry, CancellationToken ct = default)
        {
            Written.Add(entry);
            return Task.CompletedTask;
        }

        public Task WriteManyAsync(IReadOnlyList<AuditLogEntry> entries, CancellationToken ct = default)
        {
            Written.AddRange(entries);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task ASoftDeleteIsAudited()
    {
        // A partitioned soft delete moves the row to the _Deleted collection itself rather than
        // calling the source repository's delete, which is where soft deletes are audited -- so it
        // left no entry at all.
        await RequireReplicaSetAsync();
        var sink = new RecordingSink();
        var repo = new PartitionedRepository<SoftLedger>(_db, auditSink: sink, tenantContext: new FixedTenant("acme"));
        var ledger = new SoftLedger { Id = ObjectId.GenerateNewId(), TenantId = "acme", Reference = "TO-DELETE" };
        await repo.InsertAsync(ledger);
        sink.Written.Clear();

        await repo.DeleteByObjectIdAsync(ledger.Id, "operator-1");

        var entry = Assert.Single(sink.Written);
        Assert.Equal(AuditAction.DeletedSoft, entry.Action);
        Assert.Equal("operator-1", entry.OperatorId);
        Assert.Equal(ledger.Id.ToString(), entry.EntityId);
        Assert.Equal("SoftLedgers", entry.CollectionName);
        Assert.Equal("acme", entry.TenantId);
    }

    [Fact]
    public async Task ASoftDeleteMovesTheRowAsStored()
    {
        // The move went through the deleted collection's own insert, which stamped the row as new:
        // CreatedAtUtc became the time of the delete and Version went back to 1.
        await RequireReplicaSetAsync();
        var repo = new PartitionedRepository<SoftLedger>(_db, tenantContext: new FixedTenant("acme"));
        var ledger = new SoftLedger { Id = ObjectId.GenerateNewId(), TenantId = "acme", Reference = "KEEP-DATES" };
        await repo.InsertAsync(ledger);
        var created = DateTime.UtcNow.AddDays(-30);
        await _db.GetCollection<BsonDocument>("SoftLedgers").UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", ledger.Id),
            Builders<BsonDocument>.Update.Set("createdAtUtc", created).Set("version", 4));

        await repo.DeleteByObjectIdAsync(ledger.Id, "operator-1");

        var moved = await _db.GetCollection<BsonDocument>("SoftLedgers_Deleted")
            .Find(Builders<BsonDocument>.Filter.Eq("_id", ledger.Id)).SingleAsync();
        Assert.Equal(created, moved["createdAtUtc"].ToUniversalTime(), TimeSpan.FromSeconds(1));
        Assert.Equal(5, moved["version"].AsInt32);
        Assert.True(moved["isDeleted"].AsBoolean);
        Assert.Equal("KEEP-DATES", moved["reference"].AsString);
        Assert.Equal(0, await _db.GetCollection<BsonDocument>("SoftLedgers")
            .CountDocumentsAsync(Builders<BsonDocument>.Filter.Eq("_id", ledger.Id)));
    }

    // ── The transactional path ──────────────────────────────────────────────
    //
    // Selected by asking the server whether it supports transactions, and MongoDB supports them only
    // on a replica set. Every environment this project shipped -- its own docker-compose and the CI
    // service container -- was a standalone, so this branch could not be taken anywhere: not
    // locally, not in CI, not in a test. It was covered by reading it, and the copy-verify-delete
    // fallback is what every existing assertion above actually exercised.
    //
    // Fixing that made the fallback the branch nothing could reach, since a replica set never
    // selects it. ArchivalFallbackTests covers it from the other side, against a standalone mongod
    // on 27018; between the two suites both branches run on every CI run.
    //
    // These require a replica set and say so rather than skipping, for the same reason the Kafka
    // tests do: a suite that quietly passes without its subject is worse than no suite.

    /// <summary>True when the connected server can run multi-document transactions.</summary>
    private async Task<bool> IsReplicaSetAsync()
    {
        var hello = await _db.RunCommandAsync<BsonDocument>(new BsonDocument("hello", 1));
        return hello.Contains("setName") || hello.GetValue("msg", "").AsString == "isdbgrid";
    }

    private async Task RequireReplicaSetAsync()
    {
        if (await IsReplicaSetAsync()) return;

        Assert.Fail(
            "This test covers the transactional archival path, which MongoDB offers only on a "
            + "replica set. Start one with 'docker compose up -d mongodb'; the compose file "
            + "configures rs0 and initiates it from its health check.");
    }

    [Fact]
    public async Task TheServerUnderTestIsAReplicaSet()
    {
        // Asserted on its own so that "the transactional path is untested" cannot quietly become
        // true again by someone reverting the infrastructure. Every test below it would still pass
        // against a standalone -- via the fallback -- and report the wrong thing.
        await RequireReplicaSetAsync();
    }

    [Fact]
    public async Task TheSweepArchivesThroughATransaction()
    {
        await RequireReplicaSetAsync();

        var oldId = AgedId(3);
        await SeedRawAsync(Plural, oldId, "acme", "TX-ARCHIVE");

        var services = new ServiceCollection().BuildServiceProvider();
        var worker = new DataArchivalWorker(services, _db, NullLogger<DataArchivalWorker>.Instance);

        await worker.RunSweepAsync(CancellationToken.None);

        var archived = await _db.GetCollection<BsonDocument>($"{Plural}_{oldId.CreationTime.Year}")
            .CountDocumentsAsync(Builders<BsonDocument>.Filter.Eq("_id", oldId));
        var remaining = await _db.GetCollection<BsonDocument>(Plural)
            .CountDocumentsAsync(Builders<BsonDocument>.Filter.Eq("_id", oldId));

        Assert.Equal(1, archived);
        Assert.Equal(0, remaining);
    }

    [Fact]
    public async Task ARecordIsNeverInBothCollectionsAfterASweep()
    {
        // What the transaction is for. The fallback copies, verifies and then deletes, so a failure
        // between copy and delete leaves the document in both places until a re-run corrects it; the
        // transactional path has no such window. Asserted as a property of the outcome rather than
        // by interrupting the sweep, because a test that has to crash the process to prove something
        // tends to prove something about the crash.
        await RequireReplicaSetAsync();

        var ids = new[] { AgedId(3), AgedId(4), AgedId(5) };
        foreach (var id in ids) await SeedRawAsync(Plural, id, "acme", $"MULTI-{id}");

        var services = new ServiceCollection().BuildServiceProvider();
        var worker = new DataArchivalWorker(services, _db, NullLogger<DataArchivalWorker>.Instance);

        await worker.RunSweepAsync(CancellationToken.None);

        foreach (var id in ids)
        {
            var inArchive = await _db.GetCollection<BsonDocument>($"{Plural}_{id.CreationTime.Year}")
                .CountDocumentsAsync(Builders<BsonDocument>.Filter.Eq("_id", id));
            var inActive = await _db.GetCollection<BsonDocument>(Plural)
                .CountDocumentsAsync(Builders<BsonDocument>.Filter.Eq("_id", id));

            Assert.Equal(1, inArchive);
            Assert.Equal(0, inActive);
        }
    }

    [Fact]
    public async Task AnArchivedRecordIsStillReadableThroughTheRepository()
    {
        // The sweep and the routing are two halves of one claim, and each was previously asserted
        // against a separately seeded fixture. This drives them end to end: seed hot, sweep, read.
        await RequireReplicaSetAsync();

        var oldId = AgedId(3);
        await SeedRawAsync(Plural, oldId, "acme", "ROUND-TRIP");

        var services = new ServiceCollection().BuildServiceProvider();
        await new DataArchivalWorker(services, _db, NullLogger<DataArchivalWorker>.Instance)
            .RunSweepAsync(CancellationToken.None);

        var read = await RepoFor("acme").GetByIdAsync(oldId);

        Assert.NotNull(read);
        Assert.Equal("ROUND-TRIP", read!.Reference);
    }
}
