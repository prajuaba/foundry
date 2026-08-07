using System.Security.Claims;
using Foundry.Core.Entities;
using Foundry.Core.Search;
using Foundry.Core.Security;
using Foundry.Core.User;
using Foundry.Mongo.Repositories;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using Xunit;

namespace FoundryMongo.Tests;

/// <summary>
/// The search translator, asked whether the filters it builds actually match rows.
/// </summary>
/// <remarks>
/// <para>
/// Every defect this class has produced was a name that matched nothing:
/// <c>match["IsDeleted"]</c> against documents storing <c>isDeleted</c>, and a criterion on
/// <c>Price</c> against documents storing <c>price</c>. Neither errored. The first meant
/// cross-collection search never excluded a soft-deleted row; the second meant it never matched one.
/// </para>
/// <para>
/// So none of these tests asserts that a stage was <em>built</em>. That is what
/// <c>CrossCollectionSearchAsync_BuildsCorrectPipelineDefinition</c> does — it compares the document
/// the code produces against a document the test spells out, which is the same assumption twice, and
/// it passed through both defects and has had to be corrected for each of them in turn. These tests
/// seed rows, run the translator's own pipeline through MongoDB, and assert on which rows come back.
/// MongoDB is here as the oracle for "does this match", the one question a hand-written expectation
/// cannot answer. The entitlement test needs no database and does not open one.
/// </para>
/// </remarks>
public class EntitySearchTranslatorTests : IDisposable
{
    private const string CollectionName = "Products";

    private readonly string _dbName;
    private readonly MongoClient _client;
    private readonly IMongoDatabase _db;

    /// <summary>
    /// Soft-deletable, and nothing else — so the only isolation the pipeline composes is the
    /// predicate one of the two defects lived in, and a row that does not come back came back for a
    /// reason these tests are about.
    /// </summary>
    public record Product : BaseEntity<ObjectId>, ISoftDelete
    {
        public string Name { get; set; } = string.Empty;

        /// <summary>The property the second defect was found on, by name.</summary>
        public int Price { get; set; }

        /// <summary>Stored under a name that neither the property name nor its camelCasing produces.</summary>
        [BsonElement("archived_reference")]
        public string ArchivedReference { get; set; } = string.Empty;

        [SensitiveData(Category = "financial")]
        public string CardNumber { get; set; } = string.Empty;

        public bool IsDeleted { get; init; }
        public DateTime? DeletedAt { get; init; }
    }

    private sealed class FixedUser(string subject, params string[] claims) : ICurrentUserContext
    {
        public string OperatorId => subject;
        public string? OperatorName => subject;

        public ClaimsPrincipal? User
        {
            get
            {
                var list = new List<Claim> { new("sub", subject) };
                foreach (var c in claims)
                {
                    var split = c.Split('=', 2);
                    list.Add(split.Length == 2 ? new Claim(split[0], split[1]) : new Claim("role", c));
                }

                return new ClaimsPrincipal(new ClaimsIdentity(list, "Test", "sub", "role"));
            }
        }
    }

    public EntitySearchTranslatorTests()
    {
        Foundry.Mongo.Infrastructure.Conventions.MongoDbConventions.Register();
        _dbName = $"FoundryMongo_Translator_{Guid.NewGuid():N}";
        _client = new MongoClient("mongodb://localhost:27017");
        _db = _client.GetDatabase(_dbName);
    }

    public void Dispose()
    {
        try { _client.DropDatabase(_dbName); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private static EntitySearchTranslator<Product> Translator(ICurrentUserContext? user = null)
        => new(new EntityAccessPolicy<Product>(null, user));

    /// <summary>
    /// Writes rows through the typed collection, so the stored element names are the class map's and
    /// not this test's opinion of them.
    /// </summary>
    private async Task SeedAsync(params Product[] products)
        => await _db.GetCollection<Product>(CollectionName).InsertManyAsync(products);

    /// <summary>
    /// Runs the pipeline the translator builds and returns the <c>Name</c> of every row it matched.
    /// </summary>
    private async Task<List<string>> MatchedNamesAsync(params SearchCriterion[] criteria)
    {
        var request = new CrossCollectionSearchRequest
        {
            EntityTypes = [typeof(Product)],
            CollectionToEntityTypeMap = new Dictionary<string, Type> { [CollectionName] = typeof(Product) },
            Criteria = criteria
        };

        var pipeline = Translator().BuildCrossCollectionPipeline(
            request, [(typeof(Product), CollectionName)], pageNumber: 1, pageSize: 20);

        var cursor = await _db.GetCollection<BsonDocument>(CollectionName)
            .AggregateAsync<BsonDocument>(PipelineDefinition<BsonDocument, BsonDocument>.Create(pipeline));

        var facet = await cursor.FirstOrDefaultAsync();

        var names = new List<string>();
        if (facet is not null && facet.TryGetValue("data", out var data) && data.IsBsonArray)
        {
            foreach (var item in data.AsBsonArray)
            {
                names.Add(item.AsBsonDocument["Properties"].AsBsonDocument
                    .GetValue("name", string.Empty).ToString() ?? string.Empty);
            }
        }

        names.Sort(StringComparer.Ordinal);
        return names;
    }

    private static Product Row(string name, int price = 0, string archivedReference = "", bool deleted = false)
        => new()
        {
            Id = ObjectId.GenerateNewId(),
            Name = name,
            Price = price,
            ArchivedReference = archivedReference,
            IsDeleted = deleted
        };

    private static Product Card(string cardNumber)
        => new() { Id = ObjectId.GenerateNewId(), CardNumber = cardNumber };

    // ─── A criterion has to match a row ───────────────────────────────────

    /// <summary>
    /// A criterion selects the row it names and leaves the other behind.
    /// </summary>
    /// <remarks>
    /// Both halves are load-bearing. The defect made every criterion match nothing, so a test that
    /// only asserted the unwanted row was absent would have passed throughout it — "no results" is
    /// indistinguishable from "filtered correctly" unless something is also required to come back.
    /// <c>Price</c> is the property the defect was found on.
    /// </remarks>
    [Fact]
    public async Task ACriterionMatchesTheRowItNamesAndNotTheOthers()
    {
        await SeedAsync(Row("widget", price: 42), Row("gadget", price: 7));

        Assert.Equal(["widget"], await MatchedNamesAsync(SearchCriterion.Equals("Price", 42)));
    }

    /// <summary>
    /// A criterion on a property stored under an explicit element name still matches.
    /// </summary>
    /// <remarks>
    /// <c>ArchivedReference</c> is stored as <c>archived_reference</c>, which no camelCasing rule
    /// produces. An implementation that lower-cased the first letter itself — rather than asking the
    /// class map — passes the test above and fails this one, so the two together pin the name to its
    /// actual source.
    /// </remarks>
    [Fact]
    public async Task ACriterionOnARenamedPropertyMatchesTheStoredElement()
    {
        await SeedAsync(Row("kept", archivedReference: "REF-1"), Row("other", archivedReference: "REF-2"));

        Assert.Equal(
            ["kept"],
            await MatchedNamesAsync(SearchCriterion.Equals("ArchivedReference", "REF-1")));
    }

    /// <summary>
    /// A soft-deleted row is not among the matches, and its twin is.
    /// </summary>
    /// <remarks>
    /// This is the first defect exactly: the predicate was written as <c>match["IsDeleted"]</c> and
    /// the documents store <c>isDeleted</c>, so <c>{$ne: true}</c> was asked of a field no document
    /// had — and a missing field is not equal to <c>true</c>, so every soft-deleted row matched. The
    /// failure direction is the dangerous one: a filter that silently admits rows rather than one
    /// that silently drops them.
    /// </remarks>
    [Fact]
    public async Task ASoftDeletedRowIsNotMatched()
    {
        await SeedAsync(Row("kept"), Row("removed", deleted: true));

        Assert.Equal(["kept"], await MatchedNamesAsync());
    }

    /// <summary>
    /// A contains criterion matches the text it was given and not a pattern it happens to spell.
    /// </summary>
    /// <remarks>
    /// The three text operators build a <c>BsonRegularExpression</c>, so an unescaped value is a
    /// regex the caller wrote. <c>a.c</c> matching <c>abc</c> is the visible half; the reason to
    /// escape is the half that is not, which is that a caller chooses the pattern.
    /// </remarks>
    [Fact]
    public async Task AContainsCriterionTreatsItsValueAsLiteralText()
    {
        await SeedAsync(Row("a.c"), Row("abc"));

        Assert.Equal(["a.c"], await MatchedNamesAsync(SearchCriterion.Contains("Name", "a.c")));
    }

    /// <summary>
    /// An <c>In</c> criterion matches every value it names and nothing else.
    /// </summary>
    /// <remarks>
    /// The one operator whose value is a collection rather than a scalar, and therefore the one whose
    /// conversion has its own path. Naming two of three rows distinguishes "matched the list" from
    /// "matched the first entry" and from "matched everything".
    /// </remarks>
    [Fact]
    public async Task AnInCriterionMatchesEveryValueItNames()
    {
        await SeedAsync(Row("alpha"), Row("beta"), Row("gamma"));

        Assert.Equal(
            ["alpha", "gamma"],
            await MatchedNamesAsync(SearchCriterion.In("Name", ["alpha", "gamma"])));
    }

    // ─── Criteria are entitled before they are compiled ───────────────────

    /// <summary>
    /// A sensitive property cannot be filtered on by a caller who may not read it — and the refusal
    /// happens before any expression exists.
    /// </summary>
    /// <remarks>
    /// The translator owns the sequencing: it asks the access policy first and builds second. Were
    /// the order reversed, or the call dropped, an expression on <c>CardNumber</c> would be returned
    /// and run, and filtering is a read — a caller who cannot see the value can still learn it a
    /// prefix at a time. The permitted case is asserted too, because refusing everything would
    /// satisfy the first half and make the feature useless.
    /// </remarks>
    [Fact]
    public void SensitiveCriteriaAreRefusedBeforeAnExpressionIsBuilt()
    {
        SearchCriterion[] criteria = [SearchCriterion.StartsWith("CardNumber", "4111")];

        var unentitled = Translator(new FixedUser("alice"));
        Assert.Throws<UnauthorizedAccessException>(() => unentitled.BuildExpression(criteria));

        var entitled = Translator(
            new FixedUser("alice", $"{ViewSensitiveDataScope.ClaimType}=view:financial"));

        var predicate = entitled.BuildExpression(criteria).Compile();

        Assert.True(predicate(Card("4111111111111111")));
        Assert.False(predicate(Card("5500000000000004")));
    }
}
