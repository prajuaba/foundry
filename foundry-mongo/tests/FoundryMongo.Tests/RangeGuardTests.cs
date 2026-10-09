using System.ComponentModel.DataAnnotations;
using Foundry.Core.Attributes;
using Foundry.Core.Entities;
using Foundry.Mongo.Repositories;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace FoundryMongo.Tests;

/// <summary>
/// A <c>[Range]</c> is an invariant on what is stored, not a rule about one route.
/// </summary>
/// <remarks>
/// It used to be checked by request validation only, so a server-side writer going straight to the
/// repository stored whatever it computed -- 351,379.5 in a field declared <c>Range(0, 1000)</c> --
/// and the API then returned a value it would have refused from a client. These assert on what is
/// stored afterwards, against a real database, because "the write was refused" is only true if
/// nothing landed.
/// </remarks>
public class RangeGuardTests : IDisposable
{
    private readonly string _dbName = $"FoundryMongo_RangeGuard_{Guid.NewGuid():N}";
    private readonly MongoClient _client = new("mongodb://localhost:27017");
    private readonly IMongoDatabase _db;

    public record Forecast : BaseEntity<ObjectId>
    {
        // int operands on a decimal property, as the schema compiler emits them.
        [Range(0, 1000)]
        public decimal CoveragePercent { get; init; }

        [Range(1, 10)]
        public int PriorityWeight { get; init; } = 1;
    }

    /// <summary>Ranges whose bounds are not decimals: a date range, and the open-ended double idiom.</summary>
    public record Window : BaseEntity<ObjectId>
    {
        [Range(typeof(DateTime), "2020-01-01", "2030-12-31")]
        public DateTime Starts { get; init; } = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        [Range(0, double.MaxValue)]
        public double Weight { get; init; } = 1;
    }

    /// <summary>Archived after one year and soft-deletable, so a delete moves the row.</summary>
    [Partitioned(1)]
    public record Snapshot : BaseEntity<ObjectId>, ISoftDelete
    {
        [Range(0, 1000)]
        public decimal UtilizationPercent { get; init; }

        public bool IsDeleted { get; init; }
        public DateTime? DeletedAt { get; init; }
    }

    public RangeGuardTests()
    {
        Foundry.Mongo.Infrastructure.Conventions.MongoDbConventions.Register();
        _db = _client.GetDatabase(_dbName);
    }

    public void Dispose() => _client.DropDatabase(_dbName);

    private Repository<Forecast> Forecasts() => new(_db);

    private static Forecast At(decimal coverage) => new() { Id = ObjectId.GenerateNewId(), CoveragePercent = coverage };

    private async Task<long> StoredCount() => await Forecasts().Collection.CountDocumentsAsync(FilterDefinition<Forecast>.Empty);

    [Fact]
    public async Task AnInsertOutsideItsRangeIsRefusedAndNothingIsStored()
    {
        var ex = await Assert.ThrowsAsync<ValidationException>(() => Forecasts().InsertAsync(At(351_379.5m)));

        Assert.Contains("Forecast.CoveragePercent is 351379.5, outside its declared Range(0, 1000)", ex.Message);
        Assert.Equal(0, await StoredCount());
    }

    [Fact]
    public async Task ADecimalJustOverAnIntBoundIsRefused()
    {
        // RangeAttribute.IsValid converts to the operand type, so 1000.4 rounded to 1000 and passed.
        await Assert.ThrowsAsync<ValidationException>(() => Forecasts().InsertAsync(At(1000.4m)));
    }

    [Fact]
    public async Task ValuesOnTheBoundsAreStored()
    {
        await Forecasts().InsertAsync(At(0m));
        await Forecasts().InsertAsync(At(1000m));

        Assert.Equal(2, await StoredCount());
    }

    [Fact]
    public async Task AnIntBelowItsMinimumIsRefused()
    {
        var forecast = At(50m) with { PriorityWeight = 0 };

        var ex = await Assert.ThrowsAsync<ValidationException>(() => Forecasts().InsertAsync(forecast));
        Assert.Contains("PriorityWeight", ex.Message);
    }

    [Fact]
    public async Task AReplaceOutsideItsRangeIsRefusedAndTheStoredRowIsUnchanged()
    {
        var repo = Forecasts();
        var forecast = At(80m);
        await repo.InsertAsync(forecast);
        var stored = (await repo.GetByIdAsync(forecast.Id))!;

        await Assert.ThrowsAsync<ValidationException>(() => repo.UpdateAsync(stored with { CoveragePercent = 5000m }));

        Assert.Equal(80m, (await repo.GetByIdAsync(forecast.Id))!.CoveragePercent);
    }

    [Fact]
    public async Task ASelectorUpdateOutsideItsRangeIsRefused()
    {
        var repo = Forecasts();
        var forecast = At(80m);
        await repo.InsertAsync(forecast);

        await Assert.ThrowsAsync<ValidationException>(() =>
            repo.UpdateByObjectIdAsync(forecast.Id, f => f with { CoveragePercent = 5000m }, "op"));

        Assert.Equal(80m, (await repo.GetByIdAsync(forecast.Id))!.CoveragePercent);
    }

    [Fact]
    public async Task ABatchWithOneBadRowWritesNone()
    {
        await Assert.ThrowsAsync<ValidationException>(() =>
            Forecasts().BulkInsertAsync([At(10m), At(20m), At(5000m), At(30m)]));

        Assert.Equal(0, await StoredCount());
    }

    [Fact]
    public async Task ABulkUpdateWithOneBadRowWritesNone()
    {
        var repo = Forecasts();
        await repo.BulkInsertAsync([At(10m), At(20m)]);

        await Assert.ThrowsAsync<ValidationException>(() =>
            repo.BulkUpdateManyAsync(f => true, f => f with { CoveragePercent = f.CoveragePercent == 20m ? 5000m : 11m }));

        var values = (await repo.Collection.Find(FilterDefinition<Forecast>.Empty).ToListAsync()).Select(f => f.CoveragePercent).OrderBy(v => v);
        Assert.Equal([10m, 20m], values);
    }

    [Fact]
    public async Task ARangeWhoseBoundsAreNotDecimalsIsStillJudgedByItsBounds()
    {
        // A date range and double.MaxValue cannot be compared as decimal; they used to fall to
        // "not within" and refuse every value, valid ones included.
        var repo = new Repository<Window>(_db);

        await repo.InsertAsync(new Window { Id = ObjectId.GenerateNewId() });
        await repo.InsertAsync(new Window { Id = ObjectId.GenerateNewId(), Weight = 1e300 });

        await Assert.ThrowsAsync<ValidationException>(() =>
            repo.InsertAsync(new Window { Id = ObjectId.GenerateNewId(), Starts = new DateTime(2040, 1, 1, 0, 0, 0, DateTimeKind.Utc) }));
        await Assert.ThrowsAsync<ValidationException>(() =>
            repo.InsertAsync(new Window { Id = ObjectId.GenerateNewId(), Weight = -1 }));
        await Assert.ThrowsAsync<ValidationException>(() =>
            repo.InsertAsync(new Window { Id = ObjectId.GenerateNewId(), Weight = double.NaN }));
    }

    [Fact]
    public async Task ARowStoredBeforeTheCheckCanStillBeSoftDeleted()
    {
        // A partitioned soft delete moves the row into its deleted collection by inserting it there.
        // That is not a new value, so a row already out of range must still be deletable.
        var repo = new PartitionedRepository<Snapshot>(_db);
        var legacy = new Snapshot { Id = ObjectId.GenerateNewId(), UtilizationPercent = 4000m };
        await repo.Collection.InsertOneAsync(legacy);

        await repo.DeleteByObjectIdAsync(legacy.Id, "op");

        Assert.Equal(0, await repo.Collection.CountDocumentsAsync(s => s.Id == legacy.Id));
    }
}
