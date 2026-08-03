using System.Security.Claims;
using Foundry.Core.Entities;
using Foundry.Core.Search;
using Foundry.Core.Security;
using Foundry.Core.User;
using Foundry.Mongo.Repositories;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace FoundryMongo.Tests;

/// <summary>
/// Filtering is a read, and is entitled like one.
/// </summary>
/// <remarks>
/// A caller could filter on any public property, including ones every response masks. Masking is a
/// read-time transform and the stored value is clear text, so <c>StartsWith</c> against a masked card
/// number answered the question the mask exists to refuse — one character at a time, with every
/// response correctly masked the whole way.
/// </remarks>
public class CriteriaEntitlementTests : IDisposable
{
    private readonly string _dbName;
    private readonly MongoClient _client;
    private readonly IMongoDatabase _db;

    public record Payment : BaseEntity<ObjectId>
    {
        public string Reference { get; set; } = string.Empty;

        [SensitiveData(Category = "financial")]
        public string CardNumber { get; set; } = string.Empty;

        [PiiData(PiiType.Email)]
        public string Email { get; set; } = string.Empty;
    }

    private sealed class ScopedUser(params string[] scopes) : ICurrentUserContext
    {
        public string OperatorId => "someone";
        public string? OperatorName => "someone";

        public ClaimsPrincipal? User => new(new ClaimsIdentity(
            scopes.Select(s => new Claim(ViewSensitiveDataScope.ClaimType, s)).ToList(),
            "Test"));
    }

    public CriteriaEntitlementTests()
    {
        Foundry.Mongo.Infrastructure.Conventions.MongoDbConventions.Register();
        _dbName = $"FoundryMongo_Criteria_{Guid.NewGuid():N}";
        _client = new MongoClient("mongodb://localhost:27017");
        _db = _client.GetDatabase(_dbName);
    }

    public void Dispose()
    {
        try { _client.DropDatabase(_dbName); } catch { /* best effort */ }
    }

    private Repository<Payment> PaymentsAs(params string[] scopes)
        => new(_db, userContext: new ScopedUser(scopes));

    private async Task SeedAsync()
        => await PaymentsAs().InsertAsync(new Payment
        {
            Id = ObjectId.GenerateNewId(),
            Reference = "PAY-1",
            CardNumber = "4111111111111111",
            Email = "someone@example.com"
        });

    private static SearchCriterion[] CardStartsWith(string prefix) =>
        [new SearchCriterion { Field = "CardNumber", Operator = SearchOperator.StartsWith, Value = prefix }];

    [Fact]
    public async Task AMaskedFieldCannotBeFilteredWithoutTheScopeThatUnmasksIt()
    {
        await SeedAsync();

        var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => PaymentsAs().FindByCriteriaAsync(CardStartsWith("4111")));

        Assert.Contains("CardNumber", error.Message);
        Assert.Contains("view:financial", error.Message);
    }

    /// <summary>The oracle itself: two prefixes that would answer differently.</summary>
    [Fact]
    public async Task NeitherPrefixLeaksWhetherItMatched()
    {
        await SeedAsync();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => PaymentsAs().FindByCriteriaAsync(CardStartsWith("4111")));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => PaymentsAs().FindByCriteriaAsync(CardStartsWith("5555")));
    }

    [Fact]
    public async Task TheScopeThatUnmasksAlsoPermitsFiltering()
    {
        await SeedAsync();

        var entitled = PaymentsAs(ViewSensitiveDataScope.For("financial"));

        var hit = await entitled.FindByCriteriaAsync(CardStartsWith("4111"));
        Assert.Single(hit);

        var miss = await entitled.FindByCriteriaAsync(CardStartsWith("5555"));
        Assert.Empty(miss);
    }

    [Fact]
    public async Task APiiFieldCannotBeFilteredWithoutTheDefaultScope()
    {
        await SeedAsync();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            PaymentsAs().FindByCriteriaAsync(
                [new SearchCriterion { Field = "Email", Operator = SearchOperator.Equals, Value = "someone@example.com" }]));

        var entitled = PaymentsAs(ViewSensitiveDataScope.ClaimValue);
        Assert.Single(await entitled.FindByCriteriaAsync(
            [new SearchCriterion { Field = "Email", Operator = SearchOperator.Equals, Value = "someone@example.com" }]));
    }

    [Fact]
    public async Task AnOrdinaryFieldIsUnaffected()
    {
        await SeedAsync();

        Assert.Single(await PaymentsAs().FindByCriteriaAsync(
            [new SearchCriterion { Field = "Reference", Operator = SearchOperator.Equals, Value = "PAY-1" }]));
    }

    [Fact]
    public async Task TheSameRuleAppliesToPagedSearch()
    {
        await SeedAsync();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            PaymentsAs().SearchPagedAsync(CardStartsWith("4111"), new Foundry.Core.Paging.PagedRequest()));
    }

    [Fact]
    public async Task TooManyCriteriaAreRefused()
    {
        await SeedAsync();

        var many = Enumerable.Range(0, 40)
            .Select(i => new SearchCriterion
            {
                Field = "Reference",
                Operator = SearchOperator.Equals,
                Value = $"PAY-{i}"
            })
            .ToArray();

        var error = await Assert.ThrowsAsync<ArgumentException>(
            () => PaymentsAs().FindByCriteriaAsync(many));

        Assert.Contains("at most", error.Message);
    }
}
