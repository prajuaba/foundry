using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Foundry.Core.Entities;
using Foundry.Core.Security;
using Foundry.Core.Tenant;
using Foundry.Core.User;
using Foundry.Mongo.Repositories;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace Foundry.Mongo.Tests;

/// <summary>
/// Row-level ownership in the repository, against a real MongoDB.
/// </summary>
/// <remarks>
/// <para>
/// Roles decide whether a caller may use an endpoint. Ownership decides which rows they see through
/// it. Without it, any caller holding a declared role reached every row in their tenant — which is
/// adequate for a back-office tool and not for anything where users hold their own records.
/// </para>
/// <para>
/// As with tenancy, the direction that matters is <em>widening</em>, so every test here asserts on
/// what a caller must <em>not</em> see and not only on what they do.
/// </para>
/// </remarks>
public class OwnershipTests : IDisposable
{
    private readonly string _dbName;
    private readonly MongoClient _client;
    private readonly IMongoDatabase _db;

    [OwnerExemptRoles("Supervisor")]
    public record Note : BaseEntity<ObjectId>, IVersionable, IOwnedResource, ISoftDelete
    {
        public string OwnerId { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;
        public bool IsDeleted { get; init; }
        public DateTime? DeletedAt { get; init; }
    }

    /// <summary>Owner-scoped and multi-tenant, to prove the two filters compose.</summary>
    [OwnerExemptRoles("Supervisor")]
    public record Invoice : BaseEntity<ObjectId>, IVersionable, IOwnedResource, IMultiTenant
    {
        public string TenantId { get; set; } = string.Empty;
        public string OwnerId { get; set; } = string.Empty;
        public string Reference { get; set; } = string.Empty;
    }

    /// <summary>Owner-scoped with no exempt roles at all.</summary>
    public record Diary : BaseEntity<ObjectId>, IVersionable, IOwnedResource
    {
        public string OwnerId { get; set; } = string.Empty;
        public string Entry { get; set; } = string.Empty;
    }

    private sealed class FixedUser(string? subject, params string[] roles) : ICurrentUserContext
    {
        public string OperatorId => subject ?? "anonymous";
        public string? OperatorName => subject;

        public ClaimsPrincipal? User
        {
            get
            {
                if (subject is null) return new ClaimsPrincipal(new ClaimsIdentity());

                var claims = new List<Claim> { new("sub", subject) };
                claims.AddRange(roles.Select(r => new Claim("role", r)));
                return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test", "sub", "role"));
            }
        }
    }

    private sealed class FixedTenant(string? tenantId) : ITenantContext
    {
        public string? TenantId { get; private set; } = tenantId;
        public bool HasTenant => !string.IsNullOrWhiteSpace(TenantId);
        public void SetTenantId(string tenantId) => TenantId = tenantId;
    }

    public OwnershipTests()
    {
        Foundry.Mongo.Infrastructure.Conventions.MongoDbConventions.Register();
        _dbName = $"FoundryMongo_Ownership_{Guid.NewGuid():N}";
        _client = new MongoClient("mongodb://localhost:27017");
        _db = _client.GetDatabase(_dbName);
    }

    public void Dispose()
    {
        try { _client.DropDatabase(_dbName); } catch { /* cleanup is best effort */ }
    }

    private Repository<Note> NotesAs(string? subject, params string[] roles)
        => new(_db, userContext: new FixedUser(subject, roles));

    private Repository<Diary> DiaryAs(string? subject, params string[] roles)
        => new(_db, userContext: new FixedUser(subject, roles));

    private Repository<Invoice> InvoicesAs(string? subject, string tenant, params string[] roles)
        => new(_db, userContext: new FixedUser(subject, roles), tenantContext: new FixedTenant(tenant));

    private async Task<ObjectId> SeedNoteAsync(string owner, string body)
    {
        var note = new Note { Id = ObjectId.GenerateNewId(), Body = body };
        await NotesAs(owner).InsertAsync(note);
        return note.Id;
    }

    // ── Writes carry the caller's identity ──────────────────────────────────

    [Fact]
    public async Task AnInsertIsStampedWithTheAuthenticatedCaller()
    {
        var id = await SeedNoteAsync("alice", "hello");

        Assert.Equal("alice", (await NotesAs("alice").GetByIdAsync(id))!.OwnerId);
    }

    [Fact]
    public async Task ACallerSuppliedOwnerIsOverwritten()
    {
        // A caller who could set this could create rows owned by somebody else.
        var forged = new Note { Id = ObjectId.GenerateNewId(), Body = "forged", OwnerId = "bob" };
        await NotesAs("alice").InsertAsync(forged);

        Assert.Null(await NotesAs("bob").GetByIdAsync(forged.Id));
        Assert.Equal("alice", (await NotesAs("alice").GetByIdAsync(forged.Id))!.OwnerId);
    }

    [Fact]
    public async Task AWriteWithNoAuthenticatedCallerIsRefused()
    {
        // A row owned by nobody is unreachable to every non-exempt caller and accumulates silently.
        var repo = NotesAs(null);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => repo.InsertAsync(new Note { Id = ObjectId.GenerateNewId(), Body = "orphan" }));

        Assert.Contains("owner-scoped", error.Message);
        Assert.Contains("sub", error.Message);
    }

    [Fact]
    public async Task AnUpdateCannotReassignOwnership()
    {
        var id = await SeedNoteAsync("alice", "mine");

        var reassigned = (await NotesAs("alice").GetByIdAsync(id))! with { OwnerId = "bob" };
        await NotesAs("alice").UpdateAsync(reassigned);

        Assert.Null(await NotesAs("bob").GetByIdAsync(id));
        Assert.Equal("alice", (await NotesAs("alice").GetByIdAsync(id))!.OwnerId);
    }

    // ── Reads ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetByIdDoesNotCrossOwners()
    {
        var bobsNote = await SeedNoteAsync("bob", "bob's");

        Assert.Null(await NotesAs("alice").GetByIdAsync(bobsNote));
        Assert.NotNull(await NotesAs("bob").GetByIdAsync(bobsNote));
    }

    [Fact]
    public async Task FindManyDoesNotCrossOwners()
    {
        // The list path. Enforcing ownership on a single-row read and not on the read of all of them
        // is the more damaging half to miss -- it is the trap the tenant filter fell into.
        await SeedNoteAsync("alice", "alice's");
        await SeedNoteAsync("bob", "bob's");

        var rows = await NotesAs("alice").FindManyAsync();

        Assert.Equal("alice's", Assert.Single(rows).Body);
    }

    [Fact]
    public async Task AFilteredFindDoesNotCrossOwners()
    {
        await SeedNoteAsync("alice", "SHARED");
        await SeedNoteAsync("bob", "SHARED");

        var rows = await NotesAs("alice").FindManyAsync(n => n.Body == "SHARED");

        Assert.Equal("alice", Assert.Single(rows).OwnerId);
    }

    [Fact]
    public async Task CountDoesNotCrossOwners()
    {
        await SeedNoteAsync("alice", "a");
        await SeedNoteAsync("bob", "b1");
        await SeedNoteAsync("bob", "b2");

        Assert.Equal(1, await NotesAs("alice").CountAsync());
        Assert.Equal(2, await NotesAs("bob").CountAsync());
    }

    // ── Writes addressed by id ──────────────────────────────────────────────

    [Fact]
    public async Task AnUpdateDoesNotCrossOwners()
    {
        var bobsNote = await SeedNoteAsync("bob", "bob's");

        var stolen = (await NotesAs("bob").GetByIdAsync(bobsNote))! with { Body = "stolen" };

        await Assert.ThrowsAsync<KeyNotFoundException>(() => NotesAs("alice").UpdateAsync(stolen));
        Assert.Equal("bob's", (await NotesAs("bob").GetByIdAsync(bobsNote))!.Body);
    }

    [Fact]
    public async Task ADeleteDoesNotCrossOwners()
    {
        var bobsNote = await SeedNoteAsync("bob", "bob's");

        await NotesAs("alice").DeleteAsync(bobsNote);

        Assert.NotNull(await NotesAs("bob").GetByIdAsync(bobsNote));
    }

    // ── Exempt roles ────────────────────────────────────────────────────────

    [Fact]
    public async Task AnExemptRoleSeesEveryRow()
    {
        // Supervisors, auditors and support staff are the reason ownership can be on by default.
        await SeedNoteAsync("alice", "alice's");
        await SeedNoteAsync("bob", "bob's");

        var rows = await NotesAs("supervisor", "Supervisor").FindManyAsync();

        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public async Task AnExemptRoleReachesAnotherCallersRowById()
    {
        var bobsNote = await SeedNoteAsync("bob", "bob's");

        Assert.NotNull(await NotesAs("supervisor", "Supervisor").GetByIdAsync(bobsNote));
    }

    [Fact]
    public async Task ARoleThatIsNotExemptGrantsNothingExtra()
    {
        await SeedNoteAsync("bob", "bob's");

        var rows = await NotesAs("alice", "Manager", "Reviewer").FindManyAsync();

        Assert.Empty(rows);
    }

    [Fact]
    public async Task AnEntityWithNoExemptRolesHasNoWayThrough()
    {
        var diary = new Diary { Id = ObjectId.GenerateNewId(), Entry = "private" };
        await DiaryAs("bob").InsertAsync(diary);

        Assert.Null(await DiaryAs("alice", "Supervisor", "Admin").GetByIdAsync(diary.Id));
    }

    [Fact]
    public async Task AnExemptRoleStillOwnsWhatItWrites()
    {
        // Exemption widens what may be read; it does not make a row ownerless.
        var note = new Note { Id = ObjectId.GenerateNewId(), Body = "supervisor's own" };
        await NotesAs("supervisor", "Supervisor").InsertAsync(note);

        Assert.Equal("supervisor", (await NotesAs("supervisor", "Supervisor").GetByIdAsync(note.Id))!.OwnerId);
    }

    // ── Composition with tenancy ────────────────────────────────────────────

    [Fact]
    public async Task OwnershipNarrowsWithinTheTenantAndNeverAcrossIt()
    {
        var acmeAlice = new Invoice { Id = ObjectId.GenerateNewId(), Reference = "ACME-ALICE" };
        await InvoicesAs("alice", "acme").InsertAsync(acmeAlice);

        var acmeBob = new Invoice { Id = ObjectId.GenerateNewId(), Reference = "ACME-BOB" };
        await InvoicesAs("bob", "acme").InsertAsync(acmeBob);

        var globexAlice = new Invoice { Id = ObjectId.GenerateNewId(), Reference = "GLOBEX-ALICE" };
        await InvoicesAs("alice", "globex").InsertAsync(globexAlice);

        // Alice in acme sees only her acme row -- not Bob's, and not her own row in another tenant.
        var rows = await InvoicesAs("alice", "acme").FindManyAsync();
        Assert.Equal("ACME-ALICE", Assert.Single(rows).Reference);
    }

    [Fact]
    public async Task AnExemptRoleIsStillConfinedToItsTenant()
    {
        // The critical interaction. Exemption lifts the owner filter and must never lift the tenant
        // filter, or a supervisor in one tenant becomes a supervisor of all of them.
        await InvoicesAs("alice", "acme").InsertAsync(
            new Invoice { Id = ObjectId.GenerateNewId(), Reference = "ACME-ALICE" });
        await InvoicesAs("bob", "globex").InsertAsync(
            new Invoice { Id = ObjectId.GenerateNewId(), Reference = "GLOBEX-BOB" });

        var rows = await InvoicesAs("supervisor", "acme", "Supervisor").FindManyAsync();

        Assert.Equal("ACME-ALICE", Assert.Single(rows).Reference);
    }

    // ── Entities that are not owner-scoped ──────────────────────────────────

    [Fact]
    public async Task AnEntityThatIsNotOwnerScopedIsUnaffected()
    {
        var repo = new Repository<Plain>(_db, userContext: new FixedUser(null));
        var row = new Plain { Id = ObjectId.GenerateNewId(), Text = "no owner here" };

        await repo.InsertAsync(row);

        Assert.NotNull(await repo.GetByIdAsync(row.Id));
        Assert.Single(await repo.FindManyAsync());
    }

    public record Plain : BaseEntity<ObjectId>, IVersionable
    {
        public string Text { get; set; } = string.Empty;
    }

    [Fact]
    public async Task WithNoUserContextAtAllOwnershipIsNotApplied()
    {
        // Deliberate, and the same accommodation tenancy makes: a repository constructed without a
        // caller is a background job or a migration, not a request. Nothing to scope to.
        var note = new Note { Id = ObjectId.GenerateNewId(), Body = "seeded" };
        await new Repository<Note>(_db).InsertAsync(note);

        Assert.Equal(1, await new Repository<Note>(_db).CountAsync());
    }
}
