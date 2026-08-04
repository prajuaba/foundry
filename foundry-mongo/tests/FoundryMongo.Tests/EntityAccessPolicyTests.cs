using System.Linq.Expressions;
using System.Security.Claims;
using Foundry.Core.Entities;
using Foundry.Core.Search;
using Foundry.Core.Security;
using Foundry.Core.Tenant;
using Foundry.Core.User;
using Foundry.Mongo.Repositories;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using Xunit;

namespace Foundry.Mongo.Tests;

/// <summary>
/// The access policy on its own, with no database anywhere.
/// </summary>
/// <remarks>
/// <para>
/// Every other test of this cluster reaches it through a repository and therefore through MongoDB,
/// which is why the rule has always been tested at the coarse end — through whichever endpoint
/// happened to reach it — and why the three defects found here were each caught by a different kind
/// of test and none by the obvious one. <see cref="EntityAccessPolicy{T}"/> depends only on a tenant
/// context, a user context and the entity type, so it can be asked directly.
/// </para>
/// <para>
/// None of these tests asserts that a filter was <em>built</em>. That is what
/// <c>CrossCollectionSearchAsync_BuildsCorrectPipelineDefinition</c> did: it passed for years and has
/// been corrected twice for naming defects that made its filter match nothing, because a test that
/// reconstructs the code's own assumptions can only confirm them. These assert on things the code
/// cannot make true by agreeing with itself — that the two overloads agree with <em>each other</em>,
/// that a predicate admits one row and rejects another, and that a name comes from the class map
/// rather than from a rule the test also knows.
/// </para>
/// </remarks>
public class EntityAccessPolicyTests
{
    // ─── Fixtures ─────────────────────────────────────────────────────────

    /// <summary>Tenanted, owned, shareable and soft-deletable: every filter the policy composes.</summary>
    [OwnerExemptRoles("Supervisor")]
    [OwnerReadExemptRoles("Auditor")]
    public record Doc : BaseEntity<ObjectId>, IMultiTenant, ISharedResource, ISoftDelete
    {
        public string TenantId { get; set; } = string.Empty;
        public string OwnerId { get; set; } = string.Empty;
        public List<string> SharedWith { get; set; } = [];
        public bool IsDeleted { get; init; }
        public DateTime? DeletedAt { get; init; }

        public string Title { get; set; } = string.Empty;

        [SensitiveData(Category = "financial")]
        public string CardNumber { get; set; } = string.Empty;

        /// <summary>Stored under a name neither the property name nor its camelCasing.</summary>
        [BsonElement("archived_reference")]
        public string ArchivedReference { get; set; } = string.Empty;
    }

    private sealed class FixedUser(string? subject, params string[] claims) : ICurrentUserContext
    {
        public string OperatorId => subject ?? "anonymous";
        public string? OperatorName => subject;

        public ClaimsPrincipal? User
        {
            get
            {
                if (subject is null) return new ClaimsPrincipal(new ClaimsIdentity());

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

    private sealed class FixedTenant(string? tenantId) : ITenantContext
    {
        public string? TenantId { get; private set; } = tenantId;
        public bool HasTenant => !string.IsNullOrWhiteSpace(TenantId);
        public void SetTenantId(string tenantId) => TenantId = tenantId;
    }

    private static EntityAccessPolicy<Doc> PolicyFor(string? tenant, string? subject, params string[] claims)
        => new(new FixedTenant(tenant), new FixedUser(subject, claims));

    private static Doc Row(string tenant, string owner, params string[] sharedWith) => new()
    {
        Id = ObjectId.GenerateNewId(),
        TenantId = tenant,
        OwnerId = owner,
        SharedWith = [.. sharedWith]
    };

    private static BsonDocument Render(FilterDefinition<Doc> filter)
    {
        var registry = BsonSerializer.SerializerRegistry;
        return filter.Render(new RenderArgs<Doc>(registry.GetSerializer<Doc>(), registry));
    }

    /// <summary>
    /// The stored fields a rendered query constrains, whatever shape it constrains them in.
    /// </summary>
    /// <remarks>
    /// Comparing rendered documents literally would compare spelling rather than meaning — the two
    /// overloads express "not deleted" as <c>{$ne: true}</c> and as <c>false</c>, which are the same
    /// predicate. Which fields a filter restricts at all is the question that matters here, and is
    /// exactly the one the drifted overload got wrong: it restricted <c>isDeleted</c> and nothing else.
    /// </remarks>
    private static SortedSet<string> ConstrainedFields(BsonDocument rendered)
    {
        var fields = new SortedSet<string>(StringComparer.Ordinal);

        void Walk(BsonValue value)
        {
            switch (value)
            {
                case BsonDocument doc:
                    foreach (var element in doc)
                    {
                        if (element.Name.StartsWith('$')) Walk(element.Value);
                        else fields.Add(element.Name);
                    }
                    break;

                case BsonArray array:
                    foreach (var item in array) Walk(item);
                    break;
            }
        }

        Walk(rendered);
        return fields;
    }

    // ─── The two overloads must agree with each other ─────────────────────

    /// <summary>
    /// Both <c>ApplyReadFilters</c> overloads restrict the same fields for the same caller.
    /// </summary>
    /// <remarks>
    /// They are two hand-written implementations of one rule, and they have already drifted: the
    /// expression overload — the one behind every generated list and count endpoint — applied soft
    /// delete and nothing else, for as long as it existed, while its twin applied the tenant filter
    /// too. Neither could be caught by testing either alone against what its own author intended.
    /// Comparing them to each other is the only assertion that fails when one of them changes.
    /// </remarks>
    [Fact]
    public void BothReadFilterOverloads_RestrictTheSameFields()
    {
        var policy = PolicyFor("acme", "alice", "groups=finance");

        var fromFilter = ConstrainedFields(Render(policy.ApplyReadFilters(Builders<Doc>.Filter.Empty)));
        var fromExpression = ConstrainedFields(Render(
            Builders<Doc>.Filter.Where(policy.ApplyReadFilters((Expression<Func<Doc, bool>>?)null))));

        Assert.Equal(fromFilter, fromExpression);

        // And they agree on something rather than on nothing: an overload that applied no filter at
        // all would also agree with one that applied no filter at all.
        Assert.Contains("tenantId", fromFilter);
        Assert.Contains("ownerId", fromFilter);
        Assert.Contains("isDeleted", fromFilter);
        Assert.Contains("sharedWith", fromFilter);
    }

    // ─── Exemption widens within a tenant, never across one ───────────────

    /// <summary>
    /// An owner-exempt role sees other callers' rows in its own tenant and none in anybody else's.
    /// </summary>
    /// <remarks>
    /// The tenant filter is applied independently of the owner filter precisely so that lifting one
    /// cannot lift the other. Asserting the widening without asserting its limit would pass for an
    /// implementation that dropped both.
    /// </remarks>
    [Fact]
    public void OwnerExemptRole_WidensWithinTheTenantAndNeverAcrossIt()
    {
        var mine = Row("acme", "alice");
        var aColleagues = Row("acme", "bob");
        var anotherTenants = Row("globex", "carol");

        var plain = PolicyFor("acme", "alice")
            .ApplyReadFilters((Expression<Func<Doc, bool>>?)null).Compile();

        Assert.True(plain(mine));
        Assert.False(plain(aColleagues));      // ownership applies to an ordinary caller
        Assert.False(plain(anotherTenants));

        var supervisor = PolicyFor("acme", "sam", "Supervisor")
            .ApplyReadFilters((Expression<Func<Doc, bool>>?)null).Compile();

        Assert.True(supervisor(mine));
        Assert.True(supervisor(aColleagues));  // widened: the owner filter is lifted
        Assert.False(supervisor(anotherTenants));   // and never past the tenant
    }

    /// <summary>
    /// A read-exempt role reads the whole tenant and still writes only its own rows.
    /// </summary>
    /// <remarks>
    /// That distinction is the entire reason <c>[OwnerReadExemptRoles]</c> exists separately from
    /// <c>[OwnerExemptRoles]</c>: an auditor who could also write past the owner filter is just an
    /// exempt role with a longer name. The read path passes <c>forWrite: false</c> and the write path
    /// <c>true</c>, and nothing but this asymmetry distinguishes them.
    /// </remarks>
    [Fact]
    public void ReadExemptRole_ReadsPastTheOwnerFilterButCannotWritePastIt()
    {
        var policy = PolicyFor("acme", "ada", "Auditor");

        var reads = policy.ApplyReadFilters((Expression<Func<Doc, bool>>?)null).Compile();
        Assert.True(reads(Row("acme", "bob")));        // somebody else's row, same tenant
        Assert.False(reads(Row("globex", "bob")));     // still never another tenant's

        var writes = ConstrainedFields(Render(policy.ScopeToOwner(Builders<Doc>.Filter.Empty)));
        Assert.Contains("ownerId", writes);

        var readFields = ConstrainedFields(Render(policy.ApplyReadFilters(Builders<Doc>.Filter.Empty)));
        Assert.DoesNotContain("ownerId", readFields);
    }

    // ─── Names come from the class map, not from a rule the test knows ────

    /// <summary>
    /// <c>ElementName</c> answers with the stored element, not with the property name.
    /// </summary>
    /// <remarks>
    /// A hand-built <c>BsonDocument</c> stage naming a field that no document carries does not error;
    /// it matches nothing, which for an isolation filter means it isolates nothing and for a search
    /// means an empty result that looks like a legitimate answer. Both have happened here.
    /// <c>ArchivedReference</c> is stored under a name that neither the property name nor its
    /// camelCasing produces, so this cannot pass by a test and an implementation applying the same
    /// convention independently.
    /// </remarks>
    [Fact]
    public void ElementName_AnswersWithTheStoredElement()
    {
        Assert.Equal("tenantId", EntityAccessPolicy<Doc>.ElementName(typeof(Doc), "TenantId"));
        Assert.Equal("isDeleted", EntityAccessPolicy<Doc>.ElementName(typeof(Doc), "IsDeleted"));
        Assert.Equal("archived_reference", EntityAccessPolicy<Doc>.ElementName(typeof(Doc), "ArchivedReference"));
    }

    // ─── Filtering is a read, and is entitled like one ────────────────────

    /// <summary>
    /// A sensitive property cannot be filtered on by a caller who may not read it, and can be by one
    /// who may.
    /// </summary>
    /// <remarks>
    /// Both halves matter. Refusing everything would satisfy the first assertion and make the feature
    /// useless; the entitlement is the same one masking uses, so the caller who sees the value in full
    /// is exactly the caller who may ask questions about it.
    /// </remarks>
    [Fact]
    public void SensitiveCriteria_AreRefusedUnlessTheCallerMayUnmaskThem()
    {
        SearchCriterion[] criteria = [SearchCriterion.StartsWith("CardNumber", "4111")];

        var unentitled = PolicyFor("acme", "alice");
        Assert.Throws<UnauthorizedAccessException>(() => unentitled.EnsureCriteriaAreFilterable(criteria));

        var entitled = PolicyFor("acme", "alice", $"{ViewSensitiveDataScope.ClaimType}=view:financial");
        entitled.EnsureCriteriaAreFilterable(criteria);   // does not throw

        // An ordinary property is unaffected either way.
        unentitled.EnsureCriteriaAreFilterable([SearchCriterion.StartsWith("Title", "Q3")]);
    }
}
