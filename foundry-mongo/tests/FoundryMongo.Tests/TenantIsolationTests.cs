using System;
using System.Linq;
using System.Threading.Tasks;
using Foundry.Core.Entities;
using Foundry.Core.Tenant;
using Foundry.Mongo.Repositories;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace Foundry.Mongo.Tests;

/// <summary>
/// Tenant isolation in the repository, against a real MongoDB.
/// </summary>
/// <remarks>
/// <para>
/// Multi-tenancy is the framework's headline claim and nothing exercised it. The compiler emitted
/// <see cref="IMultiTenant"/> entities, the validator was thorough about half-configured tenancy in
/// the IR, and the repository carried a tenant filter -- but no test had ever built a multi-tenant
/// schema (it did not compile: CS8854) and no test had ever run a query with a tenant set.
/// </para>
/// <para>
/// The failure direction that matters is <em>widening</em>. A tenant filter that is not applied
/// returns another tenant's rows with a 200 and nothing recording that isolation was skipped, which
/// is why every test here asserts on what a tenant must <em>not</em> see, and not only on what it
/// does.
/// </para>
/// </remarks>
public class TenantIsolationTests : IDisposable
{
    private readonly string _dbName;
    private readonly MongoClient _client;
    private readonly IMongoDatabase _db;

    public record Invoice : BaseEntity<ObjectId>, IVersionable, IMultiTenant, ISoftDelete
    {
        public string TenantId { get; set; } = string.Empty;
        public string Reference { get; set; } = string.Empty;
        public bool IsDeleted { get; init; }
        public DateTime? DeletedAt { get; init; }
    }

    /// <summary>An ordinary entity, to prove tenancy costs nothing when it is not asked for.</summary>
    public record Note : BaseEntity<ObjectId>, IVersionable
    {
        public string Text { get; set; } = string.Empty;
    }

    /// <summary>A tenant context that is set explicitly, rather than through the ambient AsyncLocal.</summary>
    private sealed class FixedTenant(string? tenantId) : ITenantContext
    {
        public string? TenantId { get; private set; } = tenantId;
        public bool HasTenant => !string.IsNullOrWhiteSpace(TenantId);
        public void SetTenantId(string tenantId) => TenantId = tenantId;
    }

    public TenantIsolationTests()
    {
        Foundry.Mongo.Infrastructure.Conventions.MongoDbConventions.Register();
        _dbName = $"FoundryMongo_Tenant_{Guid.NewGuid():N}";
        _client = new MongoClient("mongodb://localhost:27017");
        _db = _client.GetDatabase(_dbName);
    }

    public void Dispose()
    {
        try { _client.DropDatabase(_dbName); } catch { /* cleanup is best effort */ }
    }

    private Repository<Invoice> RepoFor(string? tenantId) =>
        new(_db, tenantContext: new FixedTenant(tenantId));

    private async Task<ObjectId> SeedAsync(string tenantId, string reference)
    {
        var repo = RepoFor(tenantId);
        var invoice = new Invoice { Id = ObjectId.GenerateNewId(), Reference = reference };
        await repo.InsertAsync(invoice);
        return invoice.Id;
    }

    // ── Writes carry the server's tenant, not the caller's ──────────────────

    [Fact]
    public async Task AnInsertIsStampedWithTheAmbientTenant()
    {
        var id = await SeedAsync("acme", "ACME-001");

        var stored = await RepoFor("acme").GetByIdAsync(id);

        Assert.Equal("acme", stored!.TenantId);
    }

    [Fact]
    public async Task ACallerSuppliedTenantIsOverwritten()
    {
        // Nothing stamped the tenant, so whatever arrived on the entity was written verbatim --
        // meaning a client could write directly into another tenant simply by naming it.
        var repo = RepoFor("acme");
        var forged = new Invoice
        {
            Id = ObjectId.GenerateNewId(),
            Reference = "FORGED",
            TenantId = "globex"
        };

        await repo.InsertAsync(forged);

        var asGlobex = await RepoFor("globex").GetByIdAsync(forged.Id);
        Assert.Null(asGlobex);

        var asAcme = await RepoFor("acme").GetByIdAsync(forged.Id);
        Assert.Equal("acme", asAcme!.TenantId);
    }

    [Fact]
    public async Task AWriteWithNoTenantIsRefused()
    {
        // A multi-tenant row belonging to no tenant is invisible to everyone once isolation is on,
        // and visible to everyone until then. Refusing the write names the missing registration
        // while there is still something to fix.
        var repo = RepoFor(null);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => repo.InsertAsync(new Invoice { Id = ObjectId.GenerateNewId(), Reference = "ORPHAN" }));

        Assert.Contains("multi-tenant", error.Message);
        Assert.Contains("TenantContextMiddleware", error.Message);
    }

    // ── Reads ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetByIdDoesNotCrossTenants()
    {
        var globexId = await SeedAsync("globex", "GLOBEX-001");

        Assert.Null(await RepoFor("acme").GetByIdAsync(globexId));
        Assert.NotNull(await RepoFor("globex").GetByIdAsync(globexId));
    }

    [Fact]
    public async Task FindManyDoesNotCrossTenants()
    {
        // The defect this suite exists for. FindManyAsync takes the *expression* overload of the
        // repository's read filter, which applied soft delete and not the tenant -- and it is the
        // method behind every generated list endpoint. Every tenant saw every tenant's rows.
        await SeedAsync("acme", "ACME-001");
        await SeedAsync("globex", "GLOBEX-001");

        var acmeRows = await RepoFor("acme").FindManyAsync();

        Assert.Equal("ACME-001", Assert.Single(acmeRows).Reference);
    }

    [Fact]
    public async Task AFilteredFindDoesNotCrossTenants()
    {
        // A caller-supplied filter must narrow *within* the tenant, never escape it.
        await SeedAsync("acme", "SHARED-REF");
        await SeedAsync("globex", "SHARED-REF");

        var rows = await RepoFor("acme").FindManyAsync(i => i.Reference == "SHARED-REF");

        Assert.Equal("acme", Assert.Single(rows).TenantId);
    }

    [Fact]
    public async Task CountDoesNotCrossTenants()
    {
        await SeedAsync("acme", "ACME-001");
        await SeedAsync("globex", "GLOBEX-001");
        await SeedAsync("globex", "GLOBEX-002");

        Assert.Equal(1, await RepoFor("acme").CountAsync());
        Assert.Equal(2, await RepoFor("globex").CountAsync());
    }

    [Fact]
    public async Task SoftDeleteAndTenantFiltersBothApply()
    {
        // The two filters compose: adding the tenant must not drop the soft-delete predicate.
        var keep = await SeedAsync("acme", "KEPT");
        var remove = await SeedAsync("acme", "REMOVED");
        await SeedAsync("globex", "GLOBEX-001");

        await RepoFor("acme").DeleteAsync(remove);

        var rows = await RepoFor("acme").FindManyAsync();

        Assert.Equal(keep, Assert.Single(rows).Id);
    }

    // ── Writes addressed by id ──────────────────────────────────────────────

    [Fact]
    public async Task AnUpdateDoesNotCrossTenants()
    {
        // An id is not a secret -- it is handed out in every list response -- so a write addressed
        // by id has to be tenant-scoped or knowing an id is enough to overwrite another tenant's row.
        var globexId = await SeedAsync("globex", "GLOBEX-001");

        var stolen = (await RepoFor("globex").GetByIdAsync(globexId))! with { Reference = "STOLEN" };

        await Assert.ThrowsAsync<KeyNotFoundException>(() => RepoFor("acme").UpdateAsync(stolen));

        var unchanged = await RepoFor("globex").GetByIdAsync(globexId);
        Assert.Equal("GLOBEX-001", unchanged!.Reference);
    }

    [Fact]
    public async Task AnUpdateCannotMoveARowBetweenTenants()
    {
        var acmeId = await SeedAsync("acme", "ACME-001");

        var moved = (await RepoFor("acme").GetByIdAsync(acmeId))! with { TenantId = "globex" };
        await RepoFor("acme").UpdateAsync(moved);

        Assert.Null(await RepoFor("globex").GetByIdAsync(acmeId));
        Assert.Equal("acme", (await RepoFor("acme").GetByIdAsync(acmeId))!.TenantId);
    }

    [Fact]
    public async Task ADeleteDoesNotCrossTenants()
    {
        var globexId = await SeedAsync("globex", "GLOBEX-001");

        await RepoFor("acme").DeleteAsync(globexId);

        Assert.NotNull(await RepoFor("globex").GetByIdAsync(globexId));
    }

    // ── Entities that are not multi-tenant ──────────────────────────────────

    [Fact]
    public async Task AnEntityThatIsNotMultiTenantIsUnaffected()
    {
        // Tenancy must not leak into ordinary entities: no stamping, no filtering, and above all
        // no throwing when a tenant happens not to be set.
        var repo = new Repository<Note>(_db, tenantContext: new FixedTenant(null));
        var note = new Note { Id = ObjectId.GenerateNewId(), Text = "no tenant here" };

        await repo.InsertAsync(note);

        Assert.NotNull(await repo.GetByIdAsync(note.Id));
        Assert.Single(await repo.FindManyAsync());
    }

    [Fact]
    public async Task WithNoTenantContextAtAllReadsAreUnfiltered()
    {
        // The pre-existing behaviour, kept deliberately: a repository constructed without a tenant
        // context is a single-tenant deployment, not a broken multi-tenant one. Background jobs and
        // migrations rely on it. Writes are still refused -- only reads stay open.
        await SeedAsync("acme", "ACME-001");
        await SeedAsync("globex", "GLOBEX-001");

        var rows = await new Repository<Invoice>(_db).FindManyAsync();

        Assert.Equal(2, rows.Count);
    }
}
