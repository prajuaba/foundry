using Foundry.Core.Attributes;
using Foundry.Core.Entities;
using Foundry.Core.Tenant;
using Foundry.Mongo.Repositories;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace FoundryMongo.Tests;

/// <summary>
/// A unique index on a soft-deleted entity counts live rows only.
/// </summary>
/// <remarks>
/// Found by Resourcify's browser tests: a squad member who was removed could never rejoin, and a
/// deleted job role, skill or holiday could never be created again. Each attempt was refused as a
/// duplicate of a row the API no longer showed, because the deleted row kept its unique value.
/// </remarks>
public class SoftDeletedUniqueIndexTests : IDisposable
{
    private readonly string _dbName = $"FoundryMongo_SoftUnique_{Guid.NewGuid():N}";
    private readonly MongoClient _client = new("mongodb://localhost:27017");
    private readonly IMongoDatabase _db;

    public record Skill : BaseEntity<ObjectId>, ISoftDelete
    {
        [Indexed(Unique = true)]
        public string Name { get; init; } = string.Empty;
        public bool IsDeleted { get; init; }
        public DateTime? DeletedAt { get; init; }
    }

    [CompoundIndex("TeamId", "ResourceId", Unique = true, Name = "ux_team_resource")]
    public record Membership : BaseEntity<ObjectId>, ISoftDelete, IMultiTenant
    {
        public string TenantId { get; set; } = string.Empty;
        public string TeamId { get; init; } = string.Empty;
        public string ResourceId { get; init; } = string.Empty;
        public bool IsDeleted { get; init; }
        public DateTime? DeletedAt { get; init; }
    }

    private sealed class FixedTenant(string tenantId) : ITenantContext
    {
        public string? TenantId { get; private set; } = tenantId;
        public bool HasTenant => true;
        public void SetTenantId(string id) => TenantId = id;
    }

    public SoftDeletedUniqueIndexTests()
    {
        Foundry.Mongo.Infrastructure.Conventions.MongoDbConventions.Register();
        _db = _client.GetDatabase(_dbName);
    }

    public void Dispose() => _client.DropDatabase(_dbName);

    private static bool IsDuplicateKey(MongoWriteException ex) => ex.WriteError.Category == ServerErrorCategory.DuplicateKey;

    private static async Task<List<BsonDocument>> Indexes<TEntity>(Repository<TEntity> repo) where TEntity : class, IEntity<ObjectId>
        => await (await repo.Collection.Indexes.ListAsync()).ToListAsync();

    [Fact]
    public async Task ADeletedRowGivesUpItsUniqueValue_WhileALiveOneKeepsIt()
    {
        var repo = new Repository<Skill>(_db);
        await repo.CreateIndexesAsync();

        var first = new Skill { Id = ObjectId.GenerateNewId(), Name = "Kotlin" };
        await repo.InsertAsync(first);
        var live = await Assert.ThrowsAsync<MongoWriteException>(() => repo.InsertAsync(new Skill { Id = ObjectId.GenerateNewId(), Name = "Kotlin" }));
        Assert.True(IsDuplicateKey(live));

        await repo.DeleteAsync(first.Id);
        await repo.InsertAsync(new Skill { Id = ObjectId.GenerateNewId(), Name = "Kotlin" });

        // Two deleted rows may share it too: a value deleted twice is still free.
        var all = await repo.Collection.Find(FilterDefinition<Skill>.Empty).ToListAsync();
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public async Task RestoringADeletedRowIsRefused_WhenALiveRowHasTakenItsValue()
    {
        var repo = new Repository<Skill>(_db);
        await repo.CreateIndexesAsync();

        var old = new Skill { Id = ObjectId.GenerateNewId(), Name = "Go" };
        await repo.InsertAsync(old);
        await repo.DeleteAsync(old.Id);
        await repo.InsertAsync(new Skill { Id = ObjectId.GenerateNewId(), Name = "Go" });

        var ex = await Assert.ThrowsAsync<MongoWriteException>(() => repo.RestoreDeletedAsync(old.Id));
        Assert.True(IsDuplicateKey(ex));
        var stored = await repo.Collection.Find(Builders<Skill>.Filter.Eq(s => s.Id, old.Id)).SingleAsync();
        Assert.True(stored.IsDeleted);
    }

    [Fact]
    public async Task RestoringADeletedRowWorks_WhenItsValueIsStillFree()
    {
        var repo = new Repository<Skill>(_db);
        await repo.CreateIndexesAsync();

        var old = new Skill { Id = ObjectId.GenerateNewId(), Name = "Rust" };
        await repo.InsertAsync(old);
        await repo.DeleteAsync(old.Id);
        await repo.RestoreDeletedAsync(old.Id);

        Assert.NotNull(await repo.GetByIdAsync(old.Id));
    }

    [Fact]
    public async Task ARemovedMemberRejoins_UnderACompoundIndexScopedToTheTenant()
    {
        var repo = new Repository<Membership>(_db, tenantContext: new FixedTenant("acme"));
        await repo.CreateIndexesAsync();

        var first = new Membership { Id = ObjectId.GenerateNewId(), TeamId = "t1", ResourceId = "r1" };
        await repo.InsertAsync(first);
        await repo.DeleteAsync(first.Id);
        await repo.InsertAsync(new Membership { Id = ObjectId.GenerateNewId(), TeamId = "t1", ResourceId = "r1" });

        var ex = await Assert.ThrowsAsync<MongoWriteException>(() => repo.InsertAsync(new Membership { Id = ObjectId.GenerateNewId(), TeamId = "t1", ResourceId = "r1" }));
        Assert.True(IsDuplicateKey(ex));

        var index = (await Indexes(repo)).Single(i => i["name"] == "ux_team_resource");
        Assert.Equal(new BsonDocument { { "tenantId", 1 }, { "teamId", 1 }, { "resourceId", 1 } }, index["key"].AsBsonDocument);
        Assert.Equal(new BsonDocument("isDeleted", false), index["partialFilterExpression"].AsBsonDocument);
    }

    [Fact]
    public async Task AUniqueIndexFromBeforeIsReplaced_SoDeletedRowsAlreadyHeldStopBlocking()
    {
        // As a database created before this change holds them: unique over every row, deleted or
        // not, under the driver's default name for the property and the declared name for the compound.
        var skills = _db.GetCollection<BsonDocument>("Skills");
        await skills.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            new BsonDocument("name", 1), new CreateIndexOptions { Unique = true }));
        await skills.InsertOneAsync(new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "name", "Scala" }, { "isDeleted", true }, { "version", 1 } });
        var memberships = _db.GetCollection<BsonDocument>("Memberships");
        await memberships.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            new BsonDocument { { "tenantId", 1 }, { "teamId", 1 }, { "resourceId", 1 } },
            new CreateIndexOptions { Unique = true, Name = "ux_team_resource" }));

        var repo = new Repository<Skill>(_db);
        await repo.CreateIndexesAsync();
        await new Repository<Membership>(_db, tenantContext: new FixedTenant("acme")).CreateIndexesAsync();

        await repo.InsertAsync(new Skill { Id = ObjectId.GenerateNewId(), Name = "Scala" });

        var skillIndex = (await Indexes(repo)).Single(i => i["name"] == "name_1");
        Assert.Equal(new BsonDocument("isDeleted", false), skillIndex["partialFilterExpression"].AsBsonDocument);
        var memberIndex = (await (await memberships.Indexes.ListAsync()).ToListAsync()).Single(i => i["name"] == "ux_team_resource");
        Assert.Equal(new BsonDocument("isDeleted", false), memberIndex["partialFilterExpression"].AsBsonDocument);
    }

    [Fact]
    public async Task APartialIndexIsLeftAloneOnTheNextStart()
    {
        var drops = new List<string>();
        var settings = MongoClientSettings.FromConnectionString("mongodb://localhost:27017");
        settings.ClusterConfigurator = cb => cb.Subscribe<MongoDB.Driver.Core.Events.CommandStartedEvent>(e =>
        {
            if (e.CommandName == "dropIndexes") lock (drops) drops.Add(e.Command.ToString());
        });
        var db = new MongoClient(settings).GetDatabase(_dbName);

        await new Repository<Skill>(db).CreateIndexesAsync();
        await new Repository<Membership>(db, tenantContext: new FixedTenant("acme")).CreateIndexesAsync();
        await new Repository<Skill>(db).CreateIndexesAsync();
        await new Repository<Membership>(db, tenantContext: new FixedTenant("acme")).CreateIndexesAsync();

        Assert.Empty(drops);
    }
}
