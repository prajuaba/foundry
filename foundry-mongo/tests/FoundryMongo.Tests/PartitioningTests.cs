using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundry.Core.Attributes;
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
    public async Task AnAgedRecordIsNotLookedForInTheActiveCollection()
    {
        // A row left in the active collection past the threshold becomes unreachable, because reads
        // route by id age alone and never fall back. Recorded because it is the failure mode of an
        // archival sweep that does not run.
        var oldId = AgedId(3);
        await SeedRawAsync(Plural, oldId, "acme", "STRANDED");

        Assert.Null(await RepoFor("acme").GetByIdAsync(oldId));
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
}
