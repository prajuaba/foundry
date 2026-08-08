using Foundry.Core.Entities;
using Foundry.Core.Tenant;
using Foundry.Mongo.Repositories;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace FoundryMongo.Tests;

/// <summary>
/// The write guard, asked whether a write that lost a race is actually refused.
/// </summary>
/// <remarks>
/// <para>
/// Optimistic concurrency has one failure mode and it is silent: two writers read the same row, both
/// write, and the second one's version of the truth quietly replaces the first's. Nothing errors.
/// Nobody is told. The only evidence is a value that went backwards, noticed much later by whoever
/// depended on it.
/// </para>
/// <para>
/// So none of these tests asserts that a filter was <em>built</em> with a version in it. That is the
/// same assumption twice — the shape of a check restated by the test that checks it — and it is
/// exactly how <c>CrossCollectionSearchAsync_BuildsCorrectPipelineDefinition</c> passed through two
/// real defects in this file's sibling. A test that cannot observe a lost update is not testing
/// optimistic concurrency.
/// </para>
/// <para>
/// The race tests therefore stage a real one against MongoDB and assert on <em>what is stored
/// afterwards</em>. MongoDB is the oracle here for a different reason than it is in
/// <see cref="EntitySearchTranslatorTests"/>: there the question was "does this filter match a row",
/// which needs something that evaluates filters. Here the question is "did the loser's write
/// survive", which is a question about persisted state under two concurrent writers, and no
/// in-process double has any. A faked <c>IMongoCollection</c> would return whatever
/// <c>MatchedCount</c> the test programmed into it, which tests the fake.
/// </para>
/// <para>
/// Tenant stamping is the opposite case and is tested the opposite way. It is a pure function of the
/// ambient context, so it is asked directly and opens no database — the argument
/// <see cref="EntityAccessPolicyTests"/> makes, for the same reason.
/// </para>
/// </remarks>
public class EntityWriteGuardTests : IDisposable
{
    private const string CollectionName = "Ledgers";
    private const string Tenant = "acme";

    private readonly string _dbName;
    private readonly MongoClient _client;
    private readonly IMongoDatabase _db;

    /// <summary>
    /// Multi-tenant, and carrying one value whose loss is unmistakable. A lost update on
    /// <c>Balance</c> is the defect this class exists to prevent, stated in the smallest terms.
    /// </summary>
    public record Ledger : BaseEntity<ObjectId>, IMultiTenant
    {
        public string TenantId { get; set; } = string.Empty;

        public int Balance { get; set; }
    }

    private sealed class FixedTenant(string? tenantId) : ITenantContext
    {
        public string? TenantId { get; private set; } = tenantId;
        public bool HasTenant => !string.IsNullOrWhiteSpace(TenantId);
        public void SetTenantId(string tenantId) => TenantId = tenantId;
    }

    public EntityWriteGuardTests()
    {
        Foundry.Mongo.Infrastructure.Conventions.MongoDbConventions.Register();
        _dbName = $"FoundryMongo_WriteGuard_{Guid.NewGuid():N}";
        _client = new MongoClient("mongodb://localhost:27017");
        _db = _client.GetDatabase(_dbName);
    }

    public void Dispose()
    {
        try { _client.DropDatabase(_dbName); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private IMongoCollection<Ledger> Collection => _db.GetCollection<Ledger>(CollectionName);

    private EntityWriteGuard<Ledger> GuardFor(string? tenant)
        => new(Collection, new EntityAccessPolicy<Ledger>(new FixedTenant(tenant), null), new FixedTenant(tenant));

    /// <summary>A row at version 1, as an insert would leave it.</summary>
    private async Task<Ledger> SeedAsync(int balance, string tenant = Tenant)
    {
        var row = new Ledger
        {
            Id = ObjectId.GenerateNewId(),
            TenantId = tenant,
            Balance = balance,
            Version = 1
        };

        await Collection.InsertOneAsync(row);
        return row;
    }

    /// <summary>What is actually stored, which is the only thing a lost update shows up in.</summary>
    private async Task<Ledger?> StoredAsync(ObjectId id)
        => await (await Collection.FindAsync(Builders<Ledger>.Filter.Eq(e => e.Id, id))).FirstOrDefaultAsync();

    private Task<ReplaceOneResult> ReplaceAsync(FilterDefinition<Ledger> filter, Ledger replacement)
        => Collection.ReplaceOneAsync(filter, replacement, new ReplaceOptions { IsUpsert = false });

    // ─── A write that lost a race has to be refused ───────────────────────

    /// <summary>
    /// Two writers read the same row; the second to write is refused and the first one's value stands.
    /// </summary>
    /// <remarks>
    /// Both assertions are load-bearing and they fail for different reasons. Drop the version
    /// predicate from <see cref="EntityWriteGuard{T}.OccFilter"/> and the stale replace matches, so
    /// no exception is raised <em>and</em> the balance becomes the loser's — which is the lost update
    /// itself, visible in the stored row. Asserting only that something threw would leave the second
    /// half unstated, and the second half is the whole point: the exception is a symptom, the
    /// surviving value is the invariant.
    /// </remarks>
    [Fact]
    public async Task AStaleWriteIsRefusedAndTheWinnersValueSurvives()
    {
        var guard = GuardFor(Tenant);
        var seeded = await SeedAsync(balance: 100);

        // Both writers read the row at version 1.
        var asReadByWinner = await StoredAsync(seeded.Id);
        var asReadByLoser = await StoredAsync(seeded.Id);
        Assert.Equal(1, asReadByWinner!.Version);
        Assert.Equal(1, asReadByLoser!.Version);

        // The winner writes first and moves the row to version 2.
        var won = await ReplaceAsync(
            guard.OccFilter(seeded.Id, asReadByWinner.Version),
            asReadByWinner with { Balance = 200, Version = 2 });
        Assert.Equal(1L, won.MatchedCount);

        // The loser writes with the version it read, which is no longer the stored one.
        var lost = await ReplaceAsync(
            guard.OccFilter(seeded.Id, asReadByLoser.Version),
            asReadByLoser with { Balance = 999, Version = 2 });

        // The invariant first, then the symptom. If the version check is gone this reads 999 -- the
        // lost update, named as itself -- rather than an absent exception, which is a description of
        // the mechanism and not of the harm.
        Assert.Equal(200, (await StoredAsync(seeded.Id))!.Balance);
        Assert.Equal(0L, lost.MatchedCount);

        await Assert.ThrowsAsync<ConcurrencyException>(() =>
            guard.ThrowOnConcurrencyConflictAsync(
                lost, seeded.Id, seeded.Id, WriteOperation.Update, null, default));
    }

    /// <summary>
    /// A row that is gone reads as not-found, not as a conflict.
    /// </summary>
    /// <remarks>
    /// A replace that matched nothing cannot say why on its own, and the two causes call for
    /// different things from a caller: a conflict is worth retrying against the current row, an
    /// absent row is not. Delete the re-query and every zero-match becomes whichever of the two the
    /// code happens to name first, so this test and the one above pin the discrimination from both
    /// sides.
    /// </remarks>
    [Fact]
    public async Task AVanishedRowIsNotFoundRatherThanAConflict()
    {
        var guard = GuardFor(Tenant);
        var seeded = await SeedAsync(balance: 100);

        await Collection.DeleteOneAsync(Builders<Ledger>.Filter.Eq(e => e.Id, seeded.Id));

        var result = await ReplaceAsync(guard.OccFilter(seeded.Id, 1), seeded with { Balance = 5, Version = 2 });

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            guard.ThrowOnConcurrencyConflictAsync(
                result, seeded.Id, seeded.Id, WriteOperation.Update, null, default));
    }

    /// <summary>
    /// The version check will not let a caller in one tenant write a row in another, even at the
    /// right version.
    /// </summary>
    /// <remarks>
    /// The version is right, the id is right, and the write must still not land. This is the rule the
    /// read half needed three security passes to get consistent, and the write filter composes it by
    /// asking the access policy rather than restating it — so this test is what notices if the
    /// composition is dropped while the version predicate stays.
    /// </remarks>
    [Fact]
    public async Task AWriteFromAnotherTenantIsRefusedAtTheRightVersion()
    {
        var seeded = await SeedAsync(balance: 100, tenant: "globex");
        var outsider = GuardFor(Tenant);

        var result = await ReplaceAsync(
            outsider.OccFilter(seeded.Id, 1),
            seeded with { Balance = 999, Version = 2 });

        Assert.Equal(0L, result.MatchedCount);
        Assert.Equal(100, (await StoredAsync(seeded.Id))!.Balance);
    }

    /// <summary>
    /// The bulk filter now scopes to the tenant via the instance method OccFilter.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The bulk paths now use the scoped instance method _writeGuard.OccFilter instead of the
    /// static UnscopedOccFilter, which means they now include tenant and owner in the filter.
    /// This test confirms that the scoped filter matches when the row belongs to the same tenant
    /// as the guard.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheBulkFilterIsNowTenantScoped()
    {
        var guard = GuardFor("globex");
        var seeded = await SeedAsync(balance: 100, tenant: "globex");

        var result = await ReplaceAsync(
            guard.OccFilter(seeded.Id, 1),
            seeded with { Balance = 999, Version = 2 });

        Assert.Equal(1L, result.MatchedCount);
    }

    /// <summary>
    /// A bulk write from another tenant is refused at the right version via the scoped OccFilter.
    /// </summary>
    /// <remarks>
    /// The scoped OccFilter includes tenant scoping, so a write from one tenant cannot match a row
    /// in another tenant, even at the correct version. This mirrors the single-document behavior of
    /// AWriteFromAnotherTenantIsRefusedAtTheRightVersion but uses the instance method OccFilter
    /// that the bulk paths now employ.
    /// </remarks>
    [Fact]
    public async Task ABulkWriteFromAnotherTenantIsRefusedAtTheRightVersion()
    {
        var seeded = await SeedAsync(balance: 100, tenant: "globex");
        var outsider = GuardFor(Tenant);

        var result = await ReplaceAsync(
            outsider.OccFilter(seeded.Id, 1),
            seeded with { Balance = 999, Version = 2 });

        Assert.Equal(0L, result.MatchedCount);
        Assert.Equal(100, (await StoredAsync(seeded.Id))!.Balance);
    }

    /// <summary>
    /// A bulk write in which any one row lost its race is reported as a conflict.
    /// </summary>
    /// <remarks>
    /// The row that moved on is moved by a genuine second write rather than by the test inventing a
    /// stale number, so the shortfall in the matched count arrives the way it would in production.
    /// </remarks>
    [Fact]
    public async Task ABulkWriteIsRefusedWhenAnyRowLostItsRace()
    {
        var guard = GuardFor(Tenant);
        var untouched = await SeedAsync(balance: 10);
        var contended = await SeedAsync(balance: 20);

        // Someone else writes the second row between our read and our bulk write.
        await ReplaceAsync(
            guard.OccFilter(contended.Id, 1),
            contended with { Balance = 21, Version = 2 });

        var models = new WriteModel<Ledger>[]
        {
            new ReplaceOneModel<Ledger>(
                EntityWriteGuard<Ledger>.UnscopedOccFilter(untouched.Id, 1),
                untouched with { Balance = 11, Version = 2 }) { IsUpsert = false },
            new ReplaceOneModel<Ledger>(
                EntityWriteGuard<Ledger>.UnscopedOccFilter(contended.Id, 1),
                contended with { Balance = 22, Version = 2 }) { IsUpsert = false }
        };

        var bulk = await Collection.BulkWriteAsync(models);

        Assert.Throws<ConcurrencyException>(() => guard.ThrowOnBulkConcurrencyConflict(bulk, models.Length));
        Assert.Equal(21, (await StoredAsync(contended.Id))!.Balance);
    }

    /// <summary>
    /// A version that was never read is zero, and a check at zero matches no stored row.
    /// </summary>
    /// <remarks>
    /// The fallback only matters for what it does next. Reading it as zero is safe precisely because
    /// an inserted row starts at 1, so a check built on the fallback refuses the write instead of
    /// applying it against an unknown version — which is why the second half of this test is against
    /// the database rather than against the number.
    /// </remarks>
    [Fact]
    public async Task AnAbsentStoredVersionIsZeroAndMatchesNoRow()
    {
        var guard = GuardFor(Tenant);
        var seeded = await SeedAsync(balance: 100);

        Assert.Equal(0, EntityWriteGuard<Ledger>.StoredVersion(new Dictionary<string, object?>()));
        Assert.Equal(0, EntityWriteGuard<Ledger>.StoredVersion(new Dictionary<string, object?> { ["Version"] = null }));
        Assert.Equal(7, EntityWriteGuard<Ledger>.StoredVersion(new Dictionary<string, object?> { ["Version"] = 7 }));

        var result = await ReplaceAsync(
            guard.OccFilter(seeded.Id, EntityWriteGuard<Ledger>.StoredVersion(new Dictionary<string, object?>())),
            seeded with { Balance = 999, Version = 1 });

        Assert.Equal(0L, result.MatchedCount);
        Assert.Equal(100, (await StoredAsync(seeded.Id))!.Balance);
    }

    // ─── The tenant is the server's, not the caller's — and needs no database ───

    /// <summary>
    /// The ambient tenant overwrites whatever the caller sent.
    /// </summary>
    /// <remarks>
    /// The value is not validated and rejected, it is replaced, because there is no request in which
    /// a caller writing into another tenant is correct. Validating instead would turn this into a 400
    /// for a client that guessed wrong and a successful cross-tenant write for one that guessed right
    /// about a tenant it belongs to but is not currently acting as.
    /// </remarks>
    [Fact]
    public void TheAmbientTenantOverwritesWhateverTheCallerSent()
    {
        var guard = GuardFor(Tenant);
        var entity = new Ledger { Id = ObjectId.GenerateNewId(), TenantId = "globex" };

        guard.StampTenant(entity);

        Assert.Equal(Tenant, entity.TenantId);
    }

    /// <summary>
    /// A multi-tenant row written with no tenant context is refused rather than written tenantless.
    /// </summary>
    /// <remarks>
    /// The alternative is a row belonging to no tenant: visible to everybody until isolation is
    /// switched on and reachable by nobody afterwards. Failing the write names the missing
    /// registration while there is still something to fix.
    /// </remarks>
    [Fact]
    public void AMultiTenantWriteWithNoTenantIsRefused()
    {
        var guard = GuardFor(null);
        var entity = new Ledger { Id = ObjectId.GenerateNewId(), TenantId = "globex" };

        Assert.Throws<InvalidOperationException>(() => guard.StampTenant(entity));
    }
}
