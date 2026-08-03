using System.Security.Claims;
using Foundry.Core.Entities;
using Foundry.Core.Security;
using Foundry.Core.Tenant;
using Foundry.Core.User;
using Foundry.Mongo.Repositories;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace FoundryMongo.Tests;

/// <summary>
/// The three read paths on <c>IRepository&lt;T&gt;</c> that composed no isolation.
/// </summary>
/// <remarks>
/// <para>
/// Eight read paths route through <c>ApplyReadFilters</c> and every write path scopes by tenant and
/// owner. These three did neither: revision history was keyed on the entity id alone,
/// cross-collection search applied the soft-delete predicate and stopped, and <c>AggregateAsync</c>
/// ran the caller's pipeline against the collection.
/// </para>
/// <para>
/// None is a generated endpoint, so reaching them takes application code — which is the reason they
/// matter rather than a reason they do not. <c>IRepository&lt;T&gt;</c> is the layer this framework
/// presents as the isolation boundary, and code calling one of these is entitled to the guarantee the
/// other eleven methods give.
/// </para>
/// </remarks>
public class UnfilteredReadPathTests : IDisposable
{
    private readonly string _dbName;
    private readonly MongoClient _client;
    private readonly IMongoDatabase _db;

    public record Invoice : BaseEntity<ObjectId>, IVersionable, IMultiTenant
    {
        public string TenantId { get; set; } = string.Empty;
        public string Reference { get; set; } = string.Empty;
    }

    public record Ledger : BaseEntity<ObjectId>, IMultiTenant
    {
        public string TenantId { get; set; } = string.Empty;
        public string Reference { get; set; } = string.Empty;
    }

    private sealed class FixedUser(string subject) : ICurrentUserContext
    {
        public string OperatorId => subject;
        public string? OperatorName => subject;
        public ClaimsPrincipal? User =>
            new(new ClaimsIdentity([new Claim("sub", subject)], "Test", "sub", "role"));
    }

    private sealed class FixedTenant(string tenantId) : ITenantContext
    {
        public string? TenantId { get; private set; } = tenantId;
        public bool HasTenant => !string.IsNullOrWhiteSpace(TenantId);
        public void SetTenantId(string tenantId) => TenantId = tenantId;
    }

    public UnfilteredReadPathTests()
    {
        Foundry.Mongo.Infrastructure.Conventions.MongoDbConventions.Register();
        _dbName = $"FoundryMongo_Unfiltered_{Guid.NewGuid():N}";
        _client = new MongoClient("mongodb://localhost:27017");
        _db = _client.GetDatabase(_dbName);
    }

    public void Dispose()
    {
        try { _client.DropDatabase(_dbName); } catch { /* best effort */ }
    }

    private Repository<Invoice> InvoicesAs(string tenant)
        => new(_db, userContext: new FixedUser("someone"), tenantContext: new FixedTenant(tenant));

    private Repository<Ledger> LedgersAs(string tenant)
        => new(_db, userContext: new FixedUser("someone"), tenantContext: new FixedTenant(tenant));

    /// <summary>Seeds an invoice in acme and gives it a second revision.</summary>
    private async Task<ObjectId> SeedAcmeInvoiceWithHistoryAsync()
    {
        var acme = InvoicesAs("acme");

        var invoice = new Invoice { Id = ObjectId.GenerateNewId(), Reference = "ACME-SECRET" };
        await acme.InsertAsync(invoice);

        await acme.UpdateByObjectIdAsync(
            invoice.Id, existing => existing with { Reference = "ACME-SECRET-V2" }, "someone");

        return invoice.Id;
    }

    // ---- revision history ----

    [Fact]
    public async Task AnotherTenantsRevisionHistoryIsNotReadable()
    {
        var id = await SeedAcmeInvoiceWithHistoryAsync();

        // Its owner can read it, so the test is about isolation rather than about revisions failing.
        Assert.NotEmpty(await InvoicesAs("acme").GetRevisionsAsync(id));

        var stolen = await InvoicesAs("globex").GetRevisionsAsync(id);

        Assert.Empty(stolen);
    }

    [Fact]
    public async Task AnotherTenantsSingleRevisionIsNotReadable()
    {
        var id = await SeedAcmeInvoiceWithHistoryAsync();

        var mine = await InvoicesAs("acme").GetRevisionByVersionAsync(id, 1);
        Assert.NotNull(mine);

        Assert.Null(await InvoicesAs("globex").GetRevisionByVersionAsync(id, 1));
    }

    /// <summary>
    /// The stored values, not merely the count.
    /// </summary>
    /// <remarks>
    /// A revision's <c>Data</c> is the document as written. Asserting the list is empty could pass
    /// while a projection still carried something; asserting the reference never appears is the claim
    /// that matters.
    /// </remarks>
    [Fact]
    public async Task NoRevisionValueCrossesTheTenantBoundary()
    {
        var id = await SeedAcmeInvoiceWithHistoryAsync();

        var stolen = await InvoicesAs("globex").GetRevisionsAsync(id);

        Assert.DoesNotContain(stolen, r => r.Data.ToString()!.Contains("ACME-SECRET"));
    }

    // ---- cross-collection search ----

    [Fact]
    public async Task CrossCollectionSearchDoesNotReturnAnotherTenantsRows()
    {
        await InvoicesAs("acme").InsertAsync(
            new Invoice { Id = ObjectId.GenerateNewId(), Reference = "ACME-INV" });
        await LedgersAs("acme").InsertAsync(
            new Ledger { Id = ObjectId.GenerateNewId(), Reference = "ACME-LED" });

        var request = new CrossCollectionSearchRequest
        {
            EntityTypes = [typeof(Invoice), typeof(Ledger)],
            CollectionToEntityTypeMap = new Dictionary<string, Type>
            {
                ["Invoices"] = typeof(Invoice),
                ["Ledgers"] = typeof(Ledger)
            },
            Criteria = []
        };

        var mine = await InvoicesAs("acme").CrossCollectionSearchAsync(request);
        Assert.Equal(2, mine.Items.Count);

        var theirs = await InvoicesAs("globex").CrossCollectionSearchAsync(request);
        Assert.Empty(theirs.Items);
    }

    // ---- aggregation ----

    [Fact]
    public async Task AnAggregationSeesOnlyTheCallersTenant()
    {
        await InvoicesAs("acme").InsertAsync(
            new Invoice { Id = ObjectId.GenerateNewId(), Reference = "ACME-1" });
        await InvoicesAs("acme").InsertAsync(
            new Invoice { Id = ObjectId.GenerateNewId(), Reference = "ACME-2" });
        await InvoicesAs("globex").InsertAsync(
            new Invoice { Id = ObjectId.GenerateNewId(), Reference = "GLOBEX-1" });

        // A count over "the whole collection" -- the shape a reporting query takes, and the one that
        // silently counted every tenant's rows.
        PipelineDefinition<Invoice, BsonDocument> pipeline = new[]
        {
            new BsonDocument("$group", new BsonDocument
            {
                { "_id", BsonNull.Value },
                { "n", new BsonDocument("$sum", 1) }
            })
        };

        var acme = await InvoicesAs("acme").AggregateAsync(pipeline);
        Assert.Equal(2, acme.Single()["n"].AsInt32);

        var globex = await InvoicesAs("globex").AggregateAsync(pipeline);
        Assert.Equal(1, globex.Single()["n"].AsInt32);
    }

    [Fact]
    public async Task AnAggregationDoesNotSeeSoftDeletedRowsOfItsOwnTenant()
    {
        var notes = new Repository<SoftNote>(_db, userContext: new FixedUser("someone"));

        var kept = new SoftNote { Id = ObjectId.GenerateNewId(), Body = "kept" };
        var removed = new SoftNote { Id = ObjectId.GenerateNewId(), Body = "removed" };
        await notes.InsertAsync(kept);
        await notes.InsertAsync(removed);
        await notes.DeleteByObjectIdAsync(removed.Id, "someone");

        PipelineDefinition<SoftNote, BsonDocument> pipeline = new[]
        {
            new BsonDocument("$group", new BsonDocument
            {
                { "_id", BsonNull.Value },
                { "n", new BsonDocument("$sum", 1) }
            })
        };

        var result = await notes.AggregateAsync(pipeline);
        Assert.Equal(1, result.Single()["n"].AsInt32);
    }

    public record SoftNote : BaseEntity<ObjectId>, ISoftDelete
    {
        public string Body { get; set; } = string.Empty;
        public bool IsDeleted { get; init; }
        public DateTime? DeletedAt { get; init; }
    }
}
