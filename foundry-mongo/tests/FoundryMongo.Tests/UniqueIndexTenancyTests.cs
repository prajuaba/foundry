using Foundry.Core.Attributes;
using Foundry.Core.Entities;
using Foundry.Core.Tenant;
using Foundry.Mongo.Repositories;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace FoundryMongo.Tests;

/// <summary>
/// A unique index on a multi-tenant entity is unique within its tenant.
/// </summary>
/// <remarks>
/// The keys used to be taken as declared, so every unique index was global. Reproduced against a
/// generated application: a second tenant creating a project code the first already used was refused
/// with 409, and the refusal told it the code existed somewhere else.
/// </remarks>
public class UniqueIndexTenancyTests : IDisposable
{
    private readonly string _dbName = $"FoundryMongo_UniqueTenancy_{Guid.NewGuid():N}";
    private readonly MongoClient _client = new("mongodb://localhost:27017");
    private readonly IMongoDatabase _db;

    public record Account : BaseEntity<ObjectId>, IMultiTenant
    {
        public string TenantId { get; set; } = string.Empty;

        [Indexed(Unique = true)]
        public string Code { get; init; } = string.Empty;
    }

    [CompoundIndex("Region", "Day", Unique = true, Name = "ux_region_day")]
    public record Holiday : BaseEntity<ObjectId>, IMultiTenant
    {
        public string TenantId { get; set; } = string.Empty;
        public string Region { get; init; } = string.Empty;
        public DateTime Day { get; init; }
    }

    /// <summary>Not multi-tenant, so its unique index stays global.</summary>
    public record Currency : BaseEntity<ObjectId>
    {
        [Indexed(Unique = true)]
        public string IsoCode { get; init; } = string.Empty;
    }

    private sealed class FixedTenant(string tenantId) : ITenantContext
    {
        public string? TenantId { get; private set; } = tenantId;
        public bool HasTenant => true;
        public void SetTenantId(string id) => TenantId = id;
    }

    public UniqueIndexTenancyTests()
    {
        Foundry.Mongo.Infrastructure.Conventions.MongoDbConventions.Register();
        _db = _client.GetDatabase(_dbName);
    }

    public void Dispose() => _client.DropDatabase(_dbName);

    private Repository<TEntity> For<TEntity>(string tenant) where TEntity : class, IEntity<ObjectId>
        => new(_db, tenantContext: new FixedTenant(tenant));

    private static bool IsDuplicateKey(MongoWriteException ex) => ex.WriteError.Category == ServerErrorCategory.DuplicateKey;

    [Fact]
    public async Task TwoTenantsMayHoldTheSameUniqueValue_ButOneTenantMayNotHoldItTwice()
    {
        var a = For<Account>("acme");
        var b = For<Account>("globex");
        await a.CreateIndexesAsync();

        await a.InsertAsync(new Account { Id = ObjectId.GenerateNewId(), Code = "PRJ-001" });
        await b.InsertAsync(new Account { Id = ObjectId.GenerateNewId(), Code = "PRJ-001" });

        var ex = await Assert.ThrowsAsync<MongoWriteException>(() => a.InsertAsync(new Account { Id = ObjectId.GenerateNewId(), Code = "PRJ-001" }));
        Assert.True(IsDuplicateKey(ex));
    }

    [Fact]
    public async Task ACompoundUniqueIndexIsScopedToo()
    {
        var day = new DateTime(2026, 4, 13, 0, 0, 0, DateTimeKind.Utc);
        var a = For<Holiday>("acme");
        await a.CreateIndexesAsync();

        await a.InsertAsync(new Holiday { Id = ObjectId.GenerateNewId(), Region = "TH", Day = day });
        await For<Holiday>("globex").InsertAsync(new Holiday { Id = ObjectId.GenerateNewId(), Region = "TH", Day = day });

        var ex = await Assert.ThrowsAsync<MongoWriteException>(() => a.InsertAsync(new Holiday { Id = ObjectId.GenerateNewId(), Region = "TH", Day = day }));
        Assert.True(IsDuplicateKey(ex));
    }

    [Fact]
    public async Task AGlobalUniqueIndexFromBeforeIsReplaced_NotLeftEnforcingBesideTheNewOne()
    {
        // As a database created before this fix holds them: the declared keys alone, under the
        // driver's default name for the property and the declared name for the compound.
        var accounts = _db.GetCollection<BsonDocument>("Accounts");
        await accounts.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            new BsonDocument("code", 1), new CreateIndexOptions { Unique = true }));
        var holidays = _db.GetCollection<BsonDocument>("Holidays");
        await holidays.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            new BsonDocument { { "region", 1 }, { "day", 1 } }, new CreateIndexOptions { Unique = true, Name = "ux_region_day" }));

        var a = For<Account>("acme");
        await a.CreateIndexesAsync();
        await For<Holiday>("acme").CreateIndexesAsync();

        await a.InsertAsync(new Account { Id = ObjectId.GenerateNewId(), Code = "PRJ-002" });
        await For<Account>("globex").InsertAsync(new Account { Id = ObjectId.GenerateNewId(), Code = "PRJ-002" });

        var keys = (await (await a.Collection.Indexes.ListAsync()).ToListAsync()).Select(i => i["key"].AsBsonDocument).ToList();
        Assert.DoesNotContain(new BsonDocument("code", 1), keys);
        Assert.Contains(new BsonDocument { { "tenantId", 1 }, { "code", 1 } }, keys);
    }

    [Fact]
    public async Task AnEntityWithNoTenantKeepsItsUniqueIndexGlobal()
    {
        var repo = new Repository<Currency>(_db);
        await repo.CreateIndexesAsync();

        await repo.InsertAsync(new Currency { Id = ObjectId.GenerateNewId(), IsoCode = "THB" });
        var ex = await Assert.ThrowsAsync<MongoWriteException>(() => repo.InsertAsync(new Currency { Id = ObjectId.GenerateNewId(), IsoCode = "THB" }));
        Assert.True(IsDuplicateKey(ex));
    }
}
