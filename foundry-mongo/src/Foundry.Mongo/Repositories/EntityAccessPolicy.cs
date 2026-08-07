using System.Linq.Expressions;
using System.Reflection;
using Foundry.Core.Entities;
using Foundry.Core.Search;
using Foundry.Core.Security;
using Foundry.Core.User;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace Foundry.Mongo.Repositories;

/// <summary>
/// What a caller is allowed to see and change — the access-policy half of <see cref="Repository{T}"/>,
/// as a collaborator rather than a region of a file.
/// </summary>
/// <remarks>
/// <para>
/// Every member here is a pure function of two contexts and the entity type. There is no collection,
/// no session and no database, which is the whole point: this cluster decides tenant isolation and
/// ownership, and until now it could only be exercised through a live MongoDB. A rule that needs a
/// database to test is a rule that gets tested at the coarse end, through whatever endpoint happens
/// to reach it, and the three defects already found here were each caught by a different kind of test
/// and none by the obvious one.
/// </para>
/// <para>
/// The members arrived unchanged from <c>Repository&lt;T&gt;</c>. That extraction was a move and not a
/// redesign — the one cluster in this codebase where a mistake is a tenant-isolation failure is the
/// worst place to combine a structural change with a semantic one — and <c>ApplyWriteFilters</c> is
/// the one member added since, in its own commit, to fix a defect the move made visible.
/// </para>
/// <para>
/// <c>CallerMaySeeRecordAsync</c> stays on the repository because it queries the collection; it asks
/// this class for the filter and runs it itself.
/// </para>
/// </remarks>
internal sealed class EntityAccessPolicy<T> where T : class, IEntity<ObjectId>
{
    private readonly Foundry.Core.Tenant.ITenantContext? _tenantContext;
    private readonly ICurrentUserContext? _userContext;

    public EntityAccessPolicy(
        Foundry.Core.Tenant.ITenantContext? tenantContext,
        ICurrentUserContext? userContext)
    {
        _tenantContext = tenantContext;
        _userContext = userContext;
    }

    /// <summary>
    /// Roles for <typeparamref name="T"/> that see every row in the tenant rather than only their own.
    /// </summary>
    /// <remarks>
    /// Read once per closed generic type. The attribute is fixed at compile time, so re-reading it
    /// per call would be reflection on every query.
    /// </remarks>
    private static readonly string[] OwnerExemptRoles =
        ((Foundry.Core.Security.OwnerExemptRolesAttribute?)Attribute.GetCustomAttribute(
            typeof(T), typeof(Foundry.Core.Security.OwnerExemptRolesAttribute)))?.Roles
        ?? Array.Empty<string>();

    /// <summary>
    /// Roles exempt from the owner filter on reads but not on writes.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="OwnerExemptRoles"/> because that one is per entity rather than per
    /// operation, so read-only oversight — an auditor, a compliance reviewer — could not be expressed
    /// at all. A role here sees the whole tenant and can still modify only its own rows.
    /// </remarks>
    private static readonly string[] OwnerReadExemptRoles =
        ((Foundry.Core.Security.OwnerReadExemptRolesAttribute?)Attribute.GetCustomAttribute(
            typeof(T), typeof(Foundry.Core.Security.OwnerReadExemptRolesAttribute)))?.Roles
        ?? Array.Empty<string>();

    private static readonly bool IsOwnerScoped =
        typeof(Foundry.Core.Security.IOwnedResource).IsAssignableFrom(typeof(T));

    private static readonly bool IsShareable =
        typeof(Foundry.Core.Security.ISharedResource).IsAssignableFrom(typeof(T));

    /// <summary>
    /// The caller's own identifier, or <c>null</c> when there is no authenticated caller.
    /// </summary>
    /// <remarks>
    /// Deliberately not <see cref="ICurrentUserContext.OperatorId"/>, which falls back to the literal
    /// "anonymous" so that audit records always carry something. That fallback is right for audit and
    /// wrong here: it would become a legitimate owner value, and every unauthenticated caller would
    /// share ownership of every row written without a caller. Ownership asks a stricter question --
    /// is there an authenticated principal at all -- and answers null when there is not.
    /// </remarks>
    private string? CurrentOwnerId
    {
        get
        {
            var principal = _userContext?.User;
            if (principal?.Identity?.IsAuthenticated != true) return null;

            var id = principal.FindFirst("sub")?.Value
                  ?? principal.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

            return string.IsNullOrWhiteSpace(id) ? null : id;
        }
    }

    /// <summary>
    /// Whether the current caller is exempt from the owner filter for this entity.
    /// </summary>
    /// <remarks>
    /// Exemption lifts the owner filter only. The tenant filter is applied independently and is never
    /// affected, so an exempt role is wider access within one tenant and never across tenants.
    /// </remarks>
    private bool IsOwnerExempt() => HoldsAnyRole(OwnerExemptRoles);

    /// <summary>
    /// Whether the caller may read past the owner filter without being able to write past it.
    /// </summary>
    private bool IsOwnerReadExempt() => HoldsAnyRole(OwnerReadExemptRoles);

    private bool HoldsAnyRole(string[] roles)
    {
        if (roles.Length == 0) return false;

        var principal = _userContext?.User;
        if (principal?.Identity?.IsAuthenticated != true) return false;

        foreach (var role in roles)
        {
            if (principal.IsInRole(role)) return true;

            // Role claims are matched by their raw name too. AddFoundryAuthentication sets
            // MapInboundClaims=false and a configurable RoleClaimType, so IsInRole alone would
            // depend on the principal having been built with a matching ClaimsIdentity role type.
            if (principal.HasClaim(c =>
                    (c.Type == "role" || c.Type == System.Security.Claims.ClaimTypes.Role)
                    && string.Equals(c.Value, role, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The identities a grant on a row can name to reach this caller: their subject and their groups.
    /// </summary>
    /// <remarks>
    /// Sharing, delegation and team scoping are the same predicate seen from three angles, differing
    /// only in what the grant names. Collapsing them here means the filter has one shape and the data
    /// layer never has to know which of the three a deployment meant.
    /// </remarks>
    private List<string> CallerIdentities()
    {
        var identities = new List<string>();

        var current = CurrentOwnerId;
        if (current is not null) identities.Add(current);

        var principal = _userContext?.User;
        if (principal is null) return identities;

        foreach (var claim in principal.Claims)
        {
            if (Array.IndexOf(Foundry.Core.Security.GroupClaims.Types, claim.Type) < 0) continue;
            if (string.IsNullOrWhiteSpace(claim.Value)) continue;
            if (identities.Contains(claim.Value)) continue;

            identities.Add(claim.Value);
        }

        return identities;
    }

    /// <summary>
    /// Whether an operation should be narrowed to the caller's own rows, and to whom.
    /// </summary>
    /// <param name="forWrite">
    /// Writes ignore read-only exemptions and grants. A grant is a read grant — see
    /// <see cref="Foundry.Core.Security.ISharedResource"/> — so a row shared with a caller is one they
    /// can see and not one they can change.
    /// </param>
    /// <param name="ownerId">The caller's own id, which a row must carry to be theirs.</param>
    /// <param name="grantedTo">
    /// Identities a grant may name to reach this caller, empty for a write or a non-shareable entity.
    /// </param>
    /// <summary>
    /// The owner scope for an entity type other than <typeparamref name="T"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="TryGetOwnerScope(bool, out string, out List{string})"/> reads statics bound to
    /// <typeparamref name="T"/>, which is right for every method that reads one collection and useless
    /// for the one that reads several. Cross-collection search unions other entity types, and each
    /// carries its own <c>[OwnerExemptRoles]</c> and its own answer to whether it is shareable — so the
    /// same question has to be asked of the type in hand rather than of T.
    /// </remarks>
    private bool TryGetOwnerScopeFor(Type entityType, out string ownerId, out List<string> grantedTo)
    {
        ownerId = string.Empty;
        grantedTo = [];

        if (!typeof(Foundry.Core.Security.IOwnedResource).IsAssignableFrom(entityType)) return false;
        if (_userContext is null) return false;

        var exempt = ((Foundry.Core.Security.OwnerExemptRolesAttribute?)Attribute.GetCustomAttribute(
            entityType, typeof(Foundry.Core.Security.OwnerExemptRolesAttribute)))?.Roles
            ?? Array.Empty<string>();

        var readExempt = ((Foundry.Core.Security.OwnerReadExemptRolesAttribute?)Attribute.GetCustomAttribute(
            entityType, typeof(Foundry.Core.Security.OwnerReadExemptRolesAttribute)))?.Roles
            ?? Array.Empty<string>();

        if (HoldsAnyRole(exempt) || HoldsAnyRole(readExempt)) return false;

        var current = CurrentOwnerId;
        if (current is null) return false;

        ownerId = current;

        if (typeof(Foundry.Core.Security.ISharedResource).IsAssignableFrom(entityType))
        {
            grantedTo = CallerIdentities();
        }

        return true;
    }

    /// <summary>
    /// Adds the tenant and ownership predicates for <paramref name="entityType"/> to an aggregation
    /// <c>$match</c> document.
    /// </summary>
    /// <remarks>
    /// Cross-collection search applied the soft-delete predicate and stopped, so it read every
    /// tenant's rows out of every collection it was given and projected <c>$$ROOT</c> — the whole
    /// document — for each. Every other search entry point on this repository is filtered; this one
    /// was not, and being the only unfiltered one made it the least likely to be noticed.
    /// </remarks>
    public void ApplyIsolationTo(BsonDocument match, Type entityType)
    {
        if (typeof(ISoftDelete).IsAssignableFrom(entityType))
        {
            match[ElementName(entityType, "IsDeleted")] = new BsonDocument("$ne", true);
        }

        if (typeof(Foundry.Core.Tenant.IMultiTenant).IsAssignableFrom(entityType)
            && _tenantContext?.HasTenant == true)
        {
            match[ElementName(entityType, "TenantId")] = _tenantContext.TenantId;
        }

        if (TryGetOwnerScopeFor(entityType, out var ownerId, out var grantedTo))
        {
            if (grantedTo.Count > 0)
            {
                // Mine, or granted to one of the identities I present -- the aggregation equivalent
                // of the Or(mine, AnyIn(SharedWith, ...)) the find path builds.
                match["$or"] = new BsonArray
                {
                    new BsonDocument(ElementName(entityType, "OwnerId"), ownerId),
                    new BsonDocument(
                        ElementName(entityType, "SharedWith"),
                        new BsonDocument("$in", new BsonArray(grantedTo)))
                };
            }
            else
            {
                match[ElementName(entityType, "OwnerId")] = ownerId;
            }
        }
    }

    /// <summary>
    /// The stored element name for a property, as the class map records it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not the property name. <c>MongoDbConventions</c> registers
    /// <c>CamelCaseElementNameConvention</c>, so <c>TenantId</c> is stored as <c>tenantId</c>. The
    /// <c>Builders&lt;T&gt;</c> filters used everywhere else resolve names through the class map and
    /// are unaffected; a hand-built <see cref="BsonDocument"/> stage is not, and a match on the wrong
    /// name does not error — it simply matches nothing.
    /// </para>
    /// <para>
    /// That is not hypothetical. The soft-delete predicate this method has always applied was written
    /// as <c>match["IsDeleted"]</c> against camelCased documents, so cross-collection search never
    /// excluded a soft-deleted row. It was found by the tenant filter added beside it failing in the
    /// same way, in a test that expected rows and got none.
    /// </para>
    /// </remarks>
    public static string ElementName(Type entityType, string propertyName)
    {
        try
        {
            var map = BsonClassMap.LookupClassMap(entityType);
            var member = map.AllMemberMaps.FirstOrDefault(m =>
                string.Equals(m.MemberName, propertyName, StringComparison.Ordinal));

            if (member is not null) return member.ElementName;
        }
        catch
        {
            // An unmappable type falls through to the convention's own rule rather than throwing:
            // this is a filter, and failing to build it must not turn into failing to apply it.
        }

        return char.ToLowerInvariant(propertyName[0]) + propertyName[1..];
    }

    private bool TryGetOwnerScope(bool forWrite, out string ownerId, out List<string> grantedTo)
    {
        ownerId = string.Empty;
        grantedTo = [];

        if (!IsOwnerScoped) return false;
        if (_userContext is null) return false;   // no caller concept at all: background jobs, migrations
        if (IsOwnerExempt()) return false;
        if (!forWrite && IsOwnerReadExempt()) return false;

        var current = CurrentOwnerId;
        if (current is null) return false;

        ownerId = current;

        if (!forWrite && IsShareable)
        {
            grantedTo = CallerIdentities();
        }

        return true;
    }

    /// <summary>
    /// Stamps the authenticated caller onto a row being written.
    /// </summary>
    /// <remarks>
    /// Server-assigned for the same reason the tenant is: a caller who could set this could create
    /// rows owned by somebody else, or hand one of their own to another user. An owner-scoped write
    /// with no authenticated caller is refused rather than left blank, because a row owned by nobody
    /// is unreachable to every non-exempt caller and silently accumulates.
    /// </remarks>
    public void StampOwner(T entity)
    {
        if (entity is not Foundry.Core.Security.IOwnedResource owned) return;
        if (_userContext is null) return;

        var current = CurrentOwnerId;

        if (current is null)
        {
            // An exempt caller acting on behalf of the system still needs an identity to write with;
            // exemption widens what may be read, not who may own a row.
            throw new InvalidOperationException(
                $"'{typeof(T).Name}' is owner-scoped, but the current request has no authenticated "
                + "caller, so the row would belong to nobody and be unreachable. Ensure the endpoint "
                + "requires authentication and that the token carries a 'sub' claim.");
        }

        owned.OwnerId = current;
    }

    /// <summary>
    /// Restricts a write targeted by id to rows the caller owns.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same reasoning as the tenant scope: an id is handed out in every list response, so a write
    /// addressed by id must be narrowed or knowing an id is enough to modify somebody else's row.
    /// </para>
    /// <para>
    /// <c>forWrite</c>, which is what makes a grant a read grant and a read-only exemption read-only.
    /// A row shared with the caller stays visible to them and unmodifiable by them, and an auditor
    /// sees the whole tenant while still writing only their own rows. Both of those hold because this
    /// call ignores what the read filter takes into account.
    /// </para>
    /// </remarks>
    public FilterDefinition<T> ScopeToOwner(FilterDefinition<T> filter)
    {
        if (!TryGetOwnerScope(forWrite: true, out var ownerId, out _)) return filter;

        return Builders<T>.Filter.And(filter, Builders<T>.Filter.Eq("OwnerId", ownerId));
    }

    /// <summary>
    /// Restricts a write targeted by id to the ambient tenant.
    /// </summary>
    /// <remarks>
    /// Reads were tenant-scoped; writes addressed by id were not. An id is not a secret -- it is
    /// handed out in every Location header and list response -- so a caller in one tenant could
    /// update, soft-delete or restore another tenant's row by naming it, and the write succeeded.
    /// With the filter applied the row is simply not found, which is also the right answer to give:
    /// a 404 does not confirm that the id exists somewhere else.
    /// </remarks>
    public FilterDefinition<T> ScopeToTenant(FilterDefinition<T> filter)
    {
        if (!typeof(Foundry.Core.Tenant.IMultiTenant).IsAssignableFrom(typeof(T))
            || _tenantContext?.HasTenant != true)
        {
            return filter;
        }

        return Builders<T>.Filter.And(
            filter,
            Builders<T>.Filter.Eq("TenantId", _tenantContext.TenantId));
    }

    /// <summary>
    /// Narrows a read to the rows the caller is allowed to see: not soft-deleted, and belonging to
    /// the ambient tenant.
    /// </summary>
    /// <remarks>
    /// Named for what it does to every read, not for one of the two filters it applies. Both
    /// overloads were previously called <c>ApplySoftDeleteFilter</c>, and that is how the tenant
    /// filter came to be missing from the expression overload for as long as it existed: the call
    /// sites read as if soft delete were the only concern, so nothing looked wrong.
    /// </remarks>
    public FilterDefinition<T> ApplyReadFilters(FilterDefinition<T> filter)
    {
        if (typeof(ISoftDelete).IsAssignableFrom(typeof(T)))
        {
            var softDeleteFilter = Builders<T>.Filter.Not(Builders<T>.Filter.Eq("IsDeleted", true));
            filter = Builders<T>.Filter.And(filter, softDeleteFilter);
        }

        if (typeof(Foundry.Core.Tenant.IMultiTenant).IsAssignableFrom(typeof(T)) && _tenantContext?.HasTenant == true)
        {
            var tenantFilter = Builders<T>.Filter.Eq("TenantId", _tenantContext.TenantId);
            filter = Builders<T>.Filter.And(filter, tenantFilter);
        }

        // Ownership narrows within the tenant; it never replaces the tenant filter above.
        if (TryGetOwnerScope(forWrite: false, out var ownerId, out var grantedTo))
        {
            var mine = Builders<T>.Filter.Eq("OwnerId", ownerId);

            // A row is the caller's, or granted to one of the identities they present. AnyIn is the
            // array-aware form: SharedWith is a list, and the row matches if any entry is one of them.
            filter = Builders<T>.Filter.And(filter, grantedTo.Count > 0
                ? Builders<T>.Filter.Or(mine, Builders<T>.Filter.AnyIn("SharedWith", grantedTo))
                : mine);
        }

        return filter;
    }

    /// <summary>
    /// Expression-tree equivalent of <see cref="ApplyReadFilters(FilterDefinition{T})"/>.
    /// </summary>
    /// <remarks>
    /// This overload applied soft delete and nothing else, while the <see cref="FilterDefinition{T}"/>
    /// one applied soft delete *and* the tenant filter. The methods behind the generated list and
    /// count endpoints -- <c>FindManyAsync</c>, <c>CountAsync</c> and <c>FindByCriteriaAsync</c> --
    /// all take an expression, so the primary read path of every multi-tenant application returned
    /// every tenant's rows with a 200 and no indication that isolation had not been applied. It could
    /// not have been noticed in passing: it was `static`, which put <c>_tenantContext</c> out of reach
    /// and made the omission look deliberate.
    /// </remarks>
    public Expression<Func<T, bool>> ApplyReadFilters(Expression<Func<T, bool>>? filter)
        => ApplyFilters(filter, forWrite: false);

    /// <summary>
    /// Narrows a predicate to the rows the caller is allowed to <em>change</em>: the read filters,
    /// with the owner scope taken on the write side.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The counterpart of <see cref="ScopeToOwner"/> for a write that names its rows with a predicate
    /// rather than with an id. <c>BulkUpdateManyAsync</c> is the only such write, and it selected its
    /// candidates with <see cref="ApplyReadFilters(Expression{Func{T, bool}})"/> — so it loaded every
    /// row the caller could <em>see</em> and replaced all of them. A read-exempt auditor overwrote
    /// another owner's row, and a <c>SharedWith</c> grantee overwrote the owner's row, both without a
    /// race and both contradicting what <c>OwnerReadExemptRolesAttribute</c> and
    /// <c>ISharedResource</c> promise in their own documentation. Every single-document write refused
    /// the same callers correctly, which is why it survived: the rule was right everywhere it was
    /// stated, and this path never stated it.
    /// </para>
    /// <para>
    /// It is fixed in the selection rather than in the version check the bulk write carries. Scoping
    /// that filter would block the same writes, but a zero-match replace routes into
    /// <see cref="EntityWriteGuard{T}.ThrowOnBulkConcurrencyConflict"/> — so an authorization failure
    /// would reach the caller as a concurrency conflict, inviting a retry that can never succeed. A
    /// row the caller may not write is simply not a candidate.
    /// </para>
    /// <para>
    /// Soft delete and the tenant filter are identical to the read side; only the owner scope differs,
    /// so the two share one body and cannot drift the way the two read overloads did.
    /// </para>
    /// </remarks>
    public Expression<Func<T, bool>> ApplyWriteFilters(Expression<Func<T, bool>>? filter)
        => ApplyFilters(filter, forWrite: true);

    private Expression<Func<T, bool>> ApplyFilters(Expression<Func<T, bool>>? filter, bool forWrite)
    {
        var parameter = filter?.Parameters[0] ?? Expression.Parameter(typeof(T), "x");
        Expression? body = filter?.Body;

        void And(Expression predicate) =>
            body = body is null ? predicate : Expression.AndAlso(body, predicate);

        if (typeof(ISoftDelete).IsAssignableFrom(typeof(T)))
        {
            And(Expression.Not(Expression.Property(parameter, nameof(ISoftDelete.IsDeleted))));
        }

        if (typeof(Foundry.Core.Tenant.IMultiTenant).IsAssignableFrom(typeof(T)) && _tenantContext?.HasTenant == true)
        {
            And(Expression.Equal(
                Expression.Property(parameter, nameof(Foundry.Core.Tenant.IMultiTenant.TenantId)),
                Expression.Constant(_tenantContext.TenantId, typeof(string))));
        }

        // This body is behind FindManyAsync, CountAsync and FindByCriteriaAsync -- every generated
        // list endpoint -- and, with forWrite set, behind BulkUpdateManyAsync's row selection.
        // Omitting the owner predicate here would leave ownership enforced on reads of a single row
        // and absent from reads of all of them, which is the more damaging half to miss. It is the
        // same trap the tenant filter fell into. The flag is what makes a grant a read grant and a
        // read-only exemption read-only, exactly as it does in ScopeToOwner.
        if (TryGetOwnerScope(forWrite, out var ownerId, out var grantedTo))
        {
            Expression visible = Expression.Equal(
                Expression.Property(parameter, nameof(Foundry.Core.Security.IOwnedResource.OwnerId)),
                Expression.Constant(ownerId, typeof(string)));

            // The disjunction must match the FilterDefinition overload's AnyIn exactly, so it is built
            // as one Contains call per identity rather than as a nested Any(...) lambda: a chain of
            // static Enumerable.Contains calls is what the MongoDB LINQ provider translates reliably,
            // and the two overloads disagreeing is the specific failure this pair has already had.
            foreach (var identity in grantedTo)
            {
                visible = Expression.OrElse(visible, Expression.Call(
                    typeof(Enumerable),
                    nameof(Enumerable.Contains),
                    [typeof(string)],
                    Expression.Property(parameter, nameof(Foundry.Core.Security.ISharedResource.SharedWith)),
                    Expression.Constant(identity, typeof(string))));
            }

            And(visible);
        }

        if (body is null) return filter ?? (x => true);

        return Expression.Lambda<Func<T, bool>>(body, parameter);
    }

    /// <summary>
    /// Whether this caller must see a masked form of a property in the given category.
    /// </summary>
    /// <remarks>
    /// Masking used to be one switch: <c>view:pii</c> unmasked every masked property on every entity,
    /// so "a claims handler may see a policy number but not a card number" could not be expressed and
    /// letting someone read one field meant letting them read all of them. A property's category
    /// names the scope that unmasks it, and a property naming no category is <c>pii</c> — so
    /// <c>view:pii</c> still means exactly what it meant.
    /// </remarks>
    public bool ShouldMask(Foundry.Core.Entities.SensitiveDataAttribute attribute)
        => _userContext?.User?.HasClaim(
               ViewSensitiveDataScope.ClaimType,
               ViewSensitiveDataScope.For(attribute.Category)) != true;

    /// <summary>
    /// Refuses to filter on a property this caller is not entitled to read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Filtering is a read. <c>[Mask]</c> and <c>[SensitiveData]</c> hide a value on the way out, but
    /// the value is stored in clear — so a caller who cannot see <c>PaymentCardNumber</c> could still
    /// ask <c>StartsWith("4111")</c> and learn from whether rows come back. Sixteen digits fall out of
    /// a few hundred requests, and every response along the way is correctly masked.
    /// </para>
    /// <para>
    /// The sharpest case is the auditor. <c>[OwnerReadExemptRoles]</c> exists to let someone read every
    /// row in a tenant while still seeing sensitive fields masked; an unrestricted filter turns that
    /// grant into "may extract every value in the tenant", which is the one thing its shape was meant
    /// to prevent.
    /// </para>
    /// <para>
    /// The entitlement is the same one <see cref="ShouldMask"/> uses, deliberately: a caller who may
    /// read the value in full may also filter on it, and the two answers come from one place so they
    /// cannot drift. Refused rather than silently dropped — a dropped predicate widens the result set,
    /// which is the wrong direction to fail.
    /// </para>
    /// </remarks>
    public void EnsureCriteriaAreFilterable(SearchCriterion[] criteria)
    {
        if (criteria is null || criteria.Length == 0) return;

        if (criteria.Length > MaxCriteriaCount)
        {
            throw new ArgumentException(
                $"A search may combine at most {MaxCriteriaCount} criteria; {criteria.Length} were supplied.",
                nameof(criteria));
        }

        foreach (var criterion in criteria)
        {
            if (string.IsNullOrWhiteSpace(criterion.Field)) continue;

            var property = typeof(T).GetProperty(
                criterion.Field,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

            if (property is null) continue;   // BuildExpression reports the unknown field itself.

            var sensitive = property.GetCustomAttribute<Foundry.Core.Entities.SensitiveDataAttribute>();
            if (sensitive is not null && ShouldMask(sensitive))
            {
                throw new UnauthorizedAccessException(
                    $"'{property.Name}' on '{typeof(T).Name}' is sensitive and this caller may not read it, "
                    + $"so it cannot be filtered on either: a filter reveals the value it is not allowed "
                    + $"to see. Hold the '{ViewSensitiveDataScope.For(sensitive.Category)}' scope to do both.");
            }

            var pii = property.GetCustomAttribute<Foundry.Core.Security.PiiDataAttribute>();
            if (pii is not null && _userContext?.User?.HasClaim(
                    ViewSensitiveDataScope.ClaimType, ViewSensitiveDataScope.ClaimValue) != true)
            {
                throw new UnauthorizedAccessException(
                    $"'{property.Name}' on '{typeof(T).Name}' is personally identifiable and this caller "
                    + $"may not read it, so it cannot be filtered on either. Hold the "
                    + $"'{ViewSensitiveDataScope.ClaimValue}' scope to do both.");
            }
        }
    }

    /// <summary>How many conditions one search may combine.</summary>
    /// <remarks>
    /// Not a security boundary — the criteria are ANDed into a filter the caller could not widen
    /// anyway. It bounds the expression tree and the query document a single request can build.
    /// </remarks>
    public const int MaxCriteriaCount = 32;
}
