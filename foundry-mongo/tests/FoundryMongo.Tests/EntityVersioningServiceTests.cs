using Foundry.Core.Entities;
using Foundry.Mongo.Repositories;
using Foundry.Mongo.Services;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace FoundryMongo.Tests;

/// <summary>
/// The revision history, asked whether what a write path stored can actually be read back.
/// </summary>
/// <remarks>
/// <para>
/// History lives in a shadow collection addressed by a derived name, and until this step every one of
/// the twelve sites that touched it derived that name for itself. Writer and reader agreed by
/// coincidence rather than by construction. The failure that shape produces is silent and it is
/// silent in the direction that hides it: a revision written where nothing reads makes
/// <c>GetRevisionsAsync</c> return an empty list, and an empty list is the honest answer for an
/// entity that has no history yet. Nothing errors, and the loss is only discovered by whoever needed
/// the audit trail, long after the writes are gone.
/// </para>
/// <para>
/// So every test here is a round trip: perform a real operation through the repository, then read the
/// history back through a reader, and assert on the revision that comes out. Nothing asserts that
/// <c>InsertOneAsync</c> was called, or on the name of the collection it was called on.
/// </para>
/// <para>
/// <b>Why a real database, and why this is a different argument from the earlier suites.</b>
/// <see cref="EntitySearchTranslatorTests"/> needs MongoDB because a <c>BsonDocument</c> filter cannot
/// be evaluated without something that evaluates them; <see cref="EntityWriteGuardTests"/> needs it
/// because a lost update is a fact about persisted state under two concurrent writers. Here the
/// question is narrower and it is about the double itself. Against a mock,
/// <c>GetCollection&lt;EntityRevision&gt;("Widgets_History")</c> is a name the <em>test</em> supplies:
/// if writer and reader both moved to the same wrong collection, a mock suite pinned to that literal
/// would still pass, because the fake has no opinion about which collection is which. A real database
/// does. It is the thing that makes "the same collection" a question rather than a stipulation, and
/// that is the entire defect class.
/// </para>
/// <para>
/// <b>Why these drive the service through <see cref="Repository{T}"/> instead of calling it directly.</b>
/// A revision that never gets written is indistinguishable, from the reader's side, from one written
/// to the wrong place — both produce nothing. Calling
/// <see cref="EntityVersioningService{T}"/> on its own could only ever prove that a save followed by a
/// load returns what was saved, which is true of any two functions that agree with each other and says
/// nothing about whether the ten write paths reach them. So the paths are the subject, and there is one
/// test per path that had no round-trip coverage before.
/// </para>
/// </remarks>
public class EntityVersioningServiceTests : IDisposable
{
    private readonly string _dbName;
    private readonly MongoClient _client;
    private readonly IMongoDatabase _db;

    /// <summary>Versioned and soft-deleting: every path except the hard delete runs against this.</summary>
    public record Widget : BaseEntity<ObjectId>, IVersionable, ISoftDelete
    {
        public string Name { get; set; } = string.Empty;
        public bool IsDeleted { get; init; }
        public DateTime? DeletedAt { get; init; }
    }

    /// <summary>Versioned but not soft-deleting, so <c>DeleteByObjectIdAsync</c> takes the hard branch.</summary>
    public record Gadget : BaseEntity<ObjectId>, IVersionable
    {
        public string Name { get; set; } = string.Empty;
    }

    /// <summary>
    /// The control. Without an entity that should produce no history, every assertion below is
    /// satisfied by a repository that writes a revision unconditionally to the right place.
    /// </summary>
    public record Trinket : BaseEntity<ObjectId>
    {
        public string Name { get; set; } = string.Empty;
    }

    public EntityVersioningServiceTests()
    {
        Foundry.Mongo.Infrastructure.Conventions.MongoDbConventions.Register();
        _dbName = $"FoundryMongo_Versioning_{Guid.NewGuid():N}";
        _client = new MongoClient("mongodb://localhost:27017");
        _db = _client.GetDatabase(_dbName);
    }

    public void Dispose()
    {
        try { _client.DropDatabase(_dbName); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private Repository<Widget> Widgets => new(_db);
    private Repository<Gadget> Gadgets => new(_db);
    private Repository<Trinket> Trinkets => new(_db);

    private static Widget NewWidget(string name) => new() { Id = ObjectId.GenerateNewId(), Name = name };

    /// <summary>The actions on an entity's history, newest first, as the repository serves them.</summary>
    private async Task<IReadOnlyList<string>> HistoryOf(Repository<Widget> repo, ObjectId id)
        => (await repo.GetRevisionsAsync(id)).Select(r => r.Action).ToList();

    // ─── The bulk paths, none of which had any history coverage ───────────

    [Fact]
    public async Task ABulkInsertLeavesARetrievableRevisionForEveryRow()
    {
        var repo = Widgets;
        var rows = new[] { NewWidget("a"), NewWidget("b"), NewWidget("c") };

        await repo.BulkInsertAsync(rows);

        foreach (var row in rows)
        {
            var revisions = await repo.GetRevisionsAsync(row.Id);

            var revision = Assert.Single(revisions);
            Assert.Equal("Insert", revision.Action);
            Assert.Equal(1, revision.Version);
        }
    }

    [Fact]
    public async Task ABulkUpdateLeavesARetrievableRevisionForEveryRow()
    {
        var repo = Widgets;
        var rows = new[] { NewWidget("a"), NewWidget("b") };
        await repo.BulkInsertAsync(rows);

        foreach (var row in rows) row.Name += "-updated";
        await repo.BulkUpdateAsync(rows);

        foreach (var row in rows)
        {
            Assert.Equal(["Update", "Insert"], await HistoryOf(repo, row.Id));
        }
    }

    [Fact]
    public async Task ASelectorBulkUpdateLeavesARetrievableRevisionForEveryRow()
    {
        var repo = Widgets;
        var rows = new[] { NewWidget("a"), NewWidget("b") };
        await repo.BulkInsertAsync(rows);

        await repo.BulkUpdateManyAsync(w => w.Name != null, existing => existing with { Name = "renamed" });

        foreach (var row in rows)
        {
            Assert.Equal(["Update", "Insert"], await HistoryOf(repo, row.Id));
        }
    }

    // ─── The single-document paths that were only ever mocked ─────────────

    [Fact]
    public async Task AWholeDocumentUpdateLeavesARetrievableRevision()
    {
        var repo = Widgets;
        var row = NewWidget("before");
        await repo.InsertAsync(row);

        row.Name = "after";
        await repo.UpdateAsync(row);

        var revisions = await repo.GetRevisionsAsync(row.Id);

        Assert.Equal(["Update", "Insert"], revisions.Select(r => r.Action));
        Assert.Equal(2, revisions[0].Version);
    }

    [Fact]
    public async Task ASoftDeleteLeavesARetrievableRevision()
    {
        var repo = Widgets;
        var row = NewWidget("doomed");
        await repo.InsertAsync(row);

        await repo.DeleteByObjectIdAsync(row.Id, "operator-1");

        // The visibility gate on history reads deliberately omits the soft-delete predicate, so a
        // soft-deleted row's history stays readable to its owner. That is what makes a restore possible.
        Assert.Equal(["SoftDelete", "Insert"], await HistoryOf(repo, row.Id));
    }

    [Fact]
    public async Task ARestoreFromSoftDeleteLeavesARetrievableRevision()
    {
        var repo = Widgets;
        var row = NewWidget("doomed");
        await repo.InsertAsync(row);
        await repo.DeleteByObjectIdAsync(row.Id, "operator-1");

        await repo.RestoreDeletedAsync(row.Id);

        Assert.Equal(["RestoreFromSoftDelete", "SoftDelete", "Insert"], await HistoryOf(repo, row.Id));
    }

    [Fact]
    public async Task AHardDeleteLeavesARetrievableRevision()
    {
        var repo = Gadgets;
        var row = new Gadget { Id = ObjectId.GenerateNewId(), Name = "doomed" };
        await repo.InsertAsync(row);

        await repo.DeleteByObjectIdAsync(row.Id, "operator-1");

        // Read through the service rather than the repository, and not to dodge an inconvenience: the
        // repository gates history on the live row still being visible, and a hard delete removes it.
        // The revision is written and is retrievable; it is simply not retrievable through the gated
        // reader, which is worth having written down. The writer still chose the collection here, so
        // this remains a round trip and still stipulates no name.
        var revisions = await new EntityVersioningService<Gadget>(_db)
            .GetRevisionsAsync(repo.CollectionName, row.Id.ToString());

        Assert.Equal(["HardDelete", "Insert"], revisions.Select(r => r.Action));
    }

    // ─── Ordering, which moved into the service with the reads ────────────

    [Fact]
    public async Task RevisionsComeBackNewestFirst()
    {
        var repo = Widgets;
        var row = NewWidget("v1");
        await repo.InsertAsync(row);

        await repo.UpdateByObjectIdAsync(row.Id, e => e with { Name = "v2" }, "operator-1");
        await repo.UpdateByObjectIdAsync(row.Id, e => e with { Name = "v3" }, "operator-1");

        var revisions = await repo.GetRevisionsAsync(row.Id);

        Assert.Equal([3, 2, 1], revisions.Select(r => r.Version));
    }

    [Fact]
    public async Task AParticularVersionIsReadBackByNumber()
    {
        var repo = Widgets;
        var row = NewWidget("v1");
        await repo.InsertAsync(row);
        await repo.UpdateByObjectIdAsync(row.Id, e => e with { Name = "v2" }, "operator-1");

        var first = await repo.GetRevisionByVersionAsync(row.Id, 1);

        Assert.NotNull(first);
        Assert.Equal("Insert", first.Action);
        Assert.Contains("v1", first.Data.ToString());
    }

    // ─── The control ──────────────────────────────────────────────────────

    /// <summary>
    /// An entity that did not ask for versioning gets no history.
    /// </summary>
    /// <remarks>
    /// Every other test above passes for a repository that writes a revision on every write to a
    /// collection both sides agree on. This is the one that fails for such a repository, which is what
    /// makes the <c>IVersionable</c> check at each of the seven write sites load-bearing rather than
    /// decorative.
    /// </remarks>
    [Fact]
    public async Task AnEntityThatDidNotAskForVersioningGetsNoHistory()
    {
        var repo = Trinkets;
        var row = new Trinket { Id = ObjectId.GenerateNewId(), Name = "plain" };

        await repo.InsertAsync(row);
        row.Name = "still plain";
        await repo.UpdateAsync(row);

        Assert.Empty(await repo.GetRevisionsAsync(row.Id));
    }
}
