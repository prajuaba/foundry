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
/// Resource-level authorization past a single owner, against a real MongoDB.
/// </summary>
/// <remarks>
/// <para>
/// Ownership answered "this row belongs to one caller" and nothing further, so sharing, delegation
/// and team scoping had to be written by hand in a business rule — where nothing checked the rule
/// reached every read path. They are one predicate here, differing only in what a grant names.
/// </para>
/// <para>
/// The direction that matters is <em>widening</em>, as with tenancy and ownership: every test asserts
/// on what a caller must <em>not</em> reach, not only on what they may. In particular a grant is a
/// read grant, so the write assertions below are the load-bearing half.
/// </para>
/// </remarks>
public class GrantTests : IDisposable
{
    private readonly string _dbName;
    private readonly MongoClient _client;
    private readonly IMongoDatabase _db;

    /// <summary>Owner-scoped, shareable, with an auditor who may read everything and change nothing.</summary>
    [OwnerExemptRoles("Supervisor")]
    [OwnerReadExemptRoles("Auditor")]
    public record Note : BaseEntity<ObjectId>, IVersionable, ISharedResource, ISoftDelete
    {
        public string OwnerId { get; set; } = string.Empty;
        public List<string> SharedWith { get; set; } = new();
        public string Body { get; set; } = string.Empty;
        public bool IsDeleted { get; init; }
        public DateTime? DeletedAt { get; init; }
    }

    /// <summary>Shareable and multi-tenant, to prove a grant never crosses a tenant.</summary>
    public record Invoice : BaseEntity<ObjectId>, IVersionable, ISharedResource, IMultiTenant
    {
        public string TenantId { get; set; } = string.Empty;
        public string OwnerId { get; set; } = string.Empty;
        public List<string> SharedWith { get; set; } = new();
        public string Reference { get; set; } = string.Empty;
    }

    private sealed class FixedUser(string? subject, string[] roles, string[] groups, string? groupClaimType = null) : ICurrentUserContext
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
                var claimType = groupClaimType ?? "groups";
                claims.AddRange(groups.Select(g => new Claim(claimType, g)));
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

    public GrantTests()
    {
        Foundry.Mongo.Infrastructure.Conventions.MongoDbConventions.Register();
        _dbName = $"FoundryMongo_Grants_{Guid.NewGuid():N}";
        _client = new MongoClient("mongodb://localhost:27017");
        _db = _client.GetDatabase(_dbName);
    }

    public void Dispose()
    {
        try { _client.DropDatabase(_dbName); } catch { /* cleanup is best effort */ }
    }

    private Repository<Note> NotesAs(string? subject, string[]? roles = null, string[]? groups = null, string? groupClaimType = null)
        => new(_db, userContext: new FixedUser(subject, roles ?? [], groups ?? [], groupClaimType));

    private Repository<Invoice> InvoicesAs(string subject, string tenant, string[]? groups = null)
        => new(_db, userContext: new FixedUser(subject, [], groups ?? []),
            tenantContext: new FixedTenant(tenant));

    private async Task<ObjectId> SeedAsync(string owner, string body, params string[] sharedWith)
    {
        var note = new Note { Id = ObjectId.GenerateNewId(), Body = body, SharedWith = [.. sharedWith] };
        await NotesAs(owner).InsertAsync(note);
        return note.Id;
    }

    // ── A grant reaches the person or team it names ─────────────────────────

    [Fact]
    public async Task ARowSharedWithACallerIsVisibleToThem()
    {
        var id = await SeedAsync("alice", "shared", "bob");

        Assert.NotNull(await NotesAs("bob").GetByIdAsync(id));
    }

    [Fact]
    public async Task ARowSharedWithACallersGroupIsVisibleToThem()
    {
        // Team scoping is the same predicate as sharing; the grant names a group instead of a person.
        var id = await SeedAsync("alice", "team", "finance");

        Assert.NotNull(await NotesAs("bob", groups: ["finance"]).GetByIdAsync(id));
    }

    [Fact]
    public async Task AGrantAppliesToListsAsWellAsReadsById()
    {
        // The two filter overloads have diverged before -- the tenant filter was applied by one and
        // not the other for as long as it existed, so the list path returned every tenant's rows.
        // Both must agree here or ownership is enforced on one row and absent on all of them.
        await SeedAsync("alice", "mine-only");
        await SeedAsync("alice", "shared-with-bob", "bob");

        var byId = await NotesAs("bob").FindManyAsync();

        Assert.Single(byId);
        Assert.Equal("shared-with-bob", byId[0].Body);
    }

    [Fact]
    public async Task AGrantNamingSomebodyElseDoesNotReachThisCaller()
    {
        var id = await SeedAsync("alice", "for-carol", "carol");

        Assert.Null(await NotesAs("bob").GetByIdAsync(id));
        Assert.Empty(await NotesAs("bob").FindManyAsync());
    }

    [Fact]
    public async Task AnUnsharedRowStaysWithItsOwner()
    {
        var id = await SeedAsync("alice", "private");

        Assert.Null(await NotesAs("bob").GetByIdAsync(id));
        Assert.NotNull(await NotesAs("alice").GetByIdAsync(id));
    }

    // ── A grant is a read grant ─────────────────────────────────────────────

    [Fact]
    public async Task ARowSharedWithACallerCannotBeUpdatedByThem()
    {
        // The half that matters. A grant that silently conferred write access would turn "let my
        // colleague see this" into "let my colleague rewrite this".
        var id = await SeedAsync("alice", "shared", "bob");

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            NotesAs("bob").UpdateByObjectIdAsync(id, n => n with { Body = "rewritten" }, "bob"));

        Assert.Equal("shared", (await NotesAs("alice").GetByIdAsync(id))!.Body);
    }

    [Fact]
    public async Task ARowSharedWithACallerCannotBeDeletedByThem()
    {
        var id = await SeedAsync("alice", "shared", "bob");

        await NotesAs("bob").DeleteByObjectIdAsync(id, "bob");

        Assert.NotNull(await NotesAs("alice").GetByIdAsync(id));
    }

    [Fact]
    public async Task ARowSharedWithACallersGroupCannotBeUpdatedByThem()
    {
        var id = await SeedAsync("alice", "team", "finance");

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            NotesAs("bob", groups: ["finance"]).UpdateByObjectIdAsync(id, n => n with { Body = "x" }, "bob"));
    }

    // ── Read-only exemption ─────────────────────────────────────────────────

    [Fact]
    public async Task AReadExemptRoleSeesEveryRow()
    {
        await SeedAsync("alice", "a");
        await SeedAsync("bob", "b");

        Assert.Equal(2, (await NotesAs("carol", roles: ["Auditor"]).FindManyAsync()).Count);
    }

    [Fact]
    public async Task AReadExemptRoleCannotChangeAnotherCallersRow()
    {
        // The case that could not be expressed at all: ownerExemptRoles is per entity, so a role
        // exempted for reads was exempted for updates and deletes too.
        var id = await SeedAsync("alice", "a");

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            NotesAs("carol", roles: ["Auditor"]).UpdateByObjectIdAsync(id, n => n with { Body = "x" }, "carol"));

        Assert.Equal("a", (await NotesAs("alice").GetByIdAsync(id))!.Body);
    }

    [Fact]
    public async Task AFullyExemptRoleCanStillChangeAnotherCallersRow()
    {
        // The existing behaviour, unchanged: read-only exemption is an addition beside it, not a
        // narrowing of it.
        var id = await SeedAsync("alice", "a");

        await NotesAs("dave", roles: ["Supervisor"]).UpdateByObjectIdAsync(id, n => n with { Body = "edited" }, "dave");

        Assert.Equal("edited", (await NotesAs("alice").GetByIdAsync(id))!.Body);
    }

    // ── The same two rules, through the bulk write path ─────────────────────

    // Everything above reaches the owner scope through a write addressed by an id. BulkUpdateManyAsync
    // is the one write that names its rows with a predicate instead, and it selected them with the
    // *read* filter -- so both rules above were enforced everywhere they were tested and absent here.
    //
    // MongoDB is the oracle for these, and deliberately, on the same reasoning the rest of this file
    // uses. The question is which rows a predicate selects and what is left in them afterwards, and
    // the predicate does not stay in .NET: it is translated by the LINQ provider into a query the
    // server evaluates. A compiled delegate would answer a question nobody asks -- what an in-process
    // Func admits -- and would agree with itself about an owner predicate that the provider dropped.
    // The harm is a row whose contents were replaced, so that is what is asserted: the stored body.

    /// <summary>
    /// A grantee's bulk update rewrites their own rows and leaves the row shared with them alone.
    /// </summary>
    /// <remarks>
    /// Both halves are the test. Bob really can see both rows, so a selection that refused everything
    /// would satisfy the assertion that matters and quietly break the feature; asserting that his own
    /// row did change is what distinguishes "scoped to what he may write" from "scoped to nothing".
    /// </remarks>
    [Fact]
    public async Task ABulkUpdateDoesNotReachARowMerelySharedWithTheCaller()
    {
        var alices = await SeedAsync("alice", "alice's shared doc", "bob");
        var bobs = await SeedAsync("bob", "bob's own");

        // Bob can read both, and asks to rewrite everything he can reach.
        Assert.Equal(2, (await NotesAs("bob").FindManyAsync()).Count);

        await NotesAs("bob").BulkUpdateManyAsync(n => n.Body != null, n => n with { Body = "OVERWRITTEN BY GRANTEE" });

        Assert.Equal("alice's shared doc", (await NotesAs("alice").GetByIdAsync(alices))!.Body);
        Assert.Equal("OVERWRITTEN BY GRANTEE", (await NotesAs("bob").GetByIdAsync(bobs))!.Body);
    }

    [Fact]
    public async Task ABulkUpdateDoesNotReachARowSharedWithTheCallersGroup()
    {
        var id = await SeedAsync("alice", "team doc", "finance");

        await NotesAs("bob", groups: ["finance"])
            .BulkUpdateManyAsync(n => n.Body != null, n => n with { Body = "OVERWRITTEN BY GROUP" });

        Assert.Equal("team doc", (await NotesAs("alice").GetByIdAsync(id))!.Body);
    }

    /// <summary>
    /// A read-exempt role's bulk update rewrites its own rows and nobody else's.
    /// </summary>
    /// <remarks>
    /// The auditor is the sharpest case: <c>[OwnerReadExemptRoles]</c> exists so that someone can be
    /// given sight of a whole tenant without being given the ability to change it, and this path
    /// handed them the second along with the first.
    /// </remarks>
    [Fact]
    public async Task ABulkUpdateByAReadExemptRoleDoesNotReachAnotherCallersRow()
    {
        var alices = await SeedAsync("alice", "alice's private note");
        var carols = await SeedAsync("carol", "carol's own note");

        var auditor = NotesAs("carol", roles: ["Auditor"]);
        Assert.Equal(2, (await auditor.FindManyAsync()).Count);   // reads the whole tenant, as intended

        await auditor.BulkUpdateManyAsync(n => n.Body != null, n => n with { Body = "OVERWRITTEN BY AUDITOR" });

        Assert.Equal("alice's private note", (await NotesAs("alice").GetByIdAsync(alices))!.Body);
        Assert.Equal("OVERWRITTEN BY AUDITOR", (await auditor.GetByIdAsync(carols))!.Body);
    }

    /// <summary>
    /// A fully exempt role still bulk-updates other callers' rows.
    /// </summary>
    /// <remarks>
    /// <c>[OwnerExemptRoles]</c> is exempt on writes as well as reads, and narrowing the selection
    /// must not quietly take that away — the fix is meant to remove write breadth that was never
    /// granted, not the breadth that was. This is the assertion that fails if the row selection is
    /// scoped by owner unconditionally rather than through the write-side scope.
    /// </remarks>
    [Fact]
    public async Task AFullyExemptRoleCanStillBulkUpdateAnotherCallersRow()
    {
        var id = await SeedAsync("alice", "a");

        await NotesAs("dave", roles: ["Supervisor"])
            .BulkUpdateManyAsync(n => n.Body != null, n => n with { Body = "edited by supervisor" });

        Assert.Equal("edited by supervisor", (await NotesAs("alice").GetByIdAsync(id))!.Body);
    }

    [Fact]
    public async Task AnOwnerCanStillBulkUpdateTheirOwnRows()
    {
        var first = await SeedAsync("alice", "one");
        var second = await SeedAsync("alice", "two");

        await NotesAs("alice").BulkUpdateManyAsync(n => n.Body != null, n => n with { Body = "rewritten" });

        Assert.Equal("rewritten", (await NotesAs("alice").GetByIdAsync(first))!.Body);
        Assert.Equal("rewritten", (await NotesAs("alice").GetByIdAsync(second))!.Body);
    }

    /// <summary>
    /// A bulk update with nothing the caller may write is an empty result, not a concurrency conflict.
    /// </summary>
    /// <remarks>
    /// This is why the fix is in the row selection rather than in the version check the bulk write
    /// carries. Scoping that filter would block exactly the same writes, but a replace that matches no
    /// row routes into <c>ThrowOnBulkConcurrencyConflict</c> — so the caller would be told their write
    /// lost a race, and invited to retry something that can never succeed however many times it is
    /// tried. A row the caller may not write is not a candidate, so there is no conflict to report and
    /// no write to retry.
    /// </remarks>
    [Fact]
    public async Task ABulkUpdateRefusedByOwnershipIsNotReportedAsAConcurrencyConflict()
    {
        var id = await SeedAsync("alice", "alice's private note");

        var auditor = NotesAs("carol", roles: ["Auditor"]);

        // Not ConcurrencyException, and not any other exception: the correct answer to "update every
        // row matching this predicate" when none of them is yours is that none of them changed.
        var results = await auditor.BulkUpdateManyAsync(n => n.Body != null, n => n with { Body = "OVERWRITTEN" });

        Assert.Empty(results);
        Assert.Equal("alice's private note", (await NotesAs("alice").GetByIdAsync(id))!.Body);
    }

    // ── A grant never widens past the tenant ────────────────────────────────

    [Fact]
    public async Task AGrantDoesNotCrossATenant()
    {
        // Exemptions have always been within-tenant only, and a grant must be too: the tenant filter
        // is applied independently of the owner filter, so widening one cannot touch the other. This
        // is the assertion that would fail if a grant were ever hoisted above the tenant predicate.
        var invoice = new Invoice { Id = ObjectId.GenerateNewId(), Reference = "ACME-1", SharedWith = ["bob"] };
        await InvoicesAs("alice", "acme").InsertAsync(invoice);

        Assert.NotNull(await InvoicesAs("bob", "acme").GetByIdAsync(invoice.Id));
        Assert.Null(await InvoicesAs("bob", "globex").GetByIdAsync(invoice.Id));
        Assert.Empty(await InvoicesAs("bob", "globex").FindManyAsync());
    }

    [Fact]
    public async Task AGroupGrantDoesNotCrossATenant()
    {
        var invoice = new Invoice { Id = ObjectId.GenerateNewId(), Reference = "ACME-2", SharedWith = ["finance"] };
        await InvoicesAs("alice", "acme").InsertAsync(invoice);

        Assert.Null(await InvoicesAs("bob", "globex", groups: ["finance"]).GetByIdAsync(invoice.Id));
    }

    // ── Interaction with the rest of the read filter ────────────────────────

    [Fact]
    public async Task ASoftDeletedRowIsNotVisibleThroughAGrant()
    {
        // A grant widens the owner predicate and nothing else. Soft delete is a separate conjunct,
        // so widening ownership must not resurrect a deleted row.
        var id = await SeedAsync("alice", "shared", "bob");
        await NotesAs("alice").DeleteByObjectIdAsync(id, "alice");

        Assert.Null(await NotesAs("bob").GetByIdAsync(id));
    }

    [Fact]
    public async Task AnUnauthenticatedReaderIsNowNarrowedByOwnershipAndGrants()
    {
        // By default, an unauthenticated caller now sees zero rows of an owner-scoped entity.
        // Background jobs, migrations and the archival worker that need unscoped read must opt in
        // explicitly via AllowUnauthenticatedFullReads. This improves the default safety posture:
        // a host that exposes a repository without authentication in front of it now gets row-level
        // filtering by default instead of complete access.
        await SeedAsync("alice", "private");

        Assert.Empty(await NotesAs(null).FindManyAsync());
    }

    [Fact]
    public async Task AGroupGrantWithCustomClaimTypeIsHonored()
    {
        Foundry.Core.Security.GroupClaims.Types = new[] { "memberOf" };
        try
        {
            var id = await SeedAsync("alice", "team-custom", "engineering");

            Assert.NotNull(await NotesAs("bob", groups: ["engineering"], groupClaimType: "memberOf").GetByIdAsync(id));
        }
        finally
        {
            Foundry.Core.Security.GroupClaims.Types = new[] { "groups", "group" };
        }
    }
}
