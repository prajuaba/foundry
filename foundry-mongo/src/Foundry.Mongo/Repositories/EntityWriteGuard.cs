using Foundry.Core.Entities;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Foundry.Mongo.Repositories;

/// <summary>
/// Which write operation is being guarded, for the sole purpose of naming it in the exception.
/// </summary>
/// <remarks>
/// The two paths phrase their failures differently and always have. Keeping the difference as one
/// argument rather than two hand-built strings is what stops the pair from drifting apart the way
/// the two <c>ApplyReadFilters</c> overloads did.
/// </remarks>
internal enum WriteOperation
{
    Update,
    Restoration
}

/// <summary>
/// What has to be true for a write to land — the write-side half of <see cref="Repository{T}"/>, as a
/// collaborator rather than five copies scattered through a file.
/// </summary>
/// <remarks>
/// <para>
/// Two invariants live here. The tenant on a row being written comes from the server's ambient
/// context and never from the request body. And a replace only lands if the stored <c>Version</c> is
/// still the one that was read, so a write that lost a race is refused rather than silently
/// overwriting the winner.
/// </para>
/// <para>
/// Both were expressed by hand at every call site, and the copies were three hundred lines apart.
/// That is the shape that has already produced three security defects in this file's read half: one
/// rule, spread out far enough that nobody can see all of it at once to notice a copy is missing.
/// The version check is the same kind of rule, and its failure mode is a lost update — silent, and
/// visible only as data that quietly went backwards.
/// </para>
/// <para>
/// Assembling the copies side by side makes one difference visible that was not before: the two
/// single-document paths and the restore path scope their check to the caller's tenant and owner,
/// and the two bulk paths do not. Hence <see cref="OccFilter"/> and
/// <see cref="UnscopedOccFilter"/> as separate, differently named members rather than one. The
/// asymmetry is preserved exactly as it was found — this is a move, not a redesign — and it is
/// reachable only by a concurrent write that changed a row's tenant without bumping its version,
/// which no path through this repository does. It is missing depth rather than an open door, and it
/// is written down here so that closing it is a decision someone makes rather than one that gets
/// made by accident.
/// </para>
/// <para>
/// Unlike the access policy and the search translator, this class does hold a collection: deciding
/// whether a zero-match replace was a conflict or a genuinely absent row requires asking the
/// database which. The session is a per-call argument, because it belongs to the caller's
/// transaction rather than to this object.
/// </para>
/// </remarks>
internal sealed class EntityWriteGuard<T> where T : class, IEntity<ObjectId>
{
    private readonly IMongoCollection<T> _collection;
    private readonly EntityAccessPolicy<T> _accessPolicy;
    private readonly Foundry.Core.Tenant.ITenantContext? _tenantContext;

    public EntityWriteGuard(
        IMongoCollection<T> collection,
        EntityAccessPolicy<T> accessPolicy,
        Foundry.Core.Tenant.ITenantContext? tenantContext)
    {
        _collection = collection;
        _accessPolicy = accessPolicy;
        _tenantContext = tenantContext;
    }

    private string CollectionName => _collection.CollectionNamespace.CollectionName;

    /// <summary>
    /// Stamps the ambient tenant onto an entity being written.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The tenant comes from the server's ambient context, never from the request body. Nothing
    /// stamped it before, so the tenant of a new row was whichever value the caller happened to
    /// send -- meaning a client could write directly into another tenant's data simply by naming
    /// it, and a client that sent nothing wrote a row with an empty tenant that later became
    /// invisible to everyone. The caller-supplied value is overwritten rather than validated,
    /// because there is no request in which a caller writing to another tenant is correct.
    /// </para>
    /// <para>
    /// A multi-tenant entity written with no tenant context throws. The alternative is a row that
    /// belongs to no tenant: it is silently unreachable once isolation is switched on, and until
    /// then it is visible to everybody. Refusing the write names the missing registration while
    /// there is still something to fix.
    /// </para>
    /// </remarks>
    public void StampTenant(T entity)
    {
        if (entity is not Foundry.Core.Tenant.IMultiTenant tenanted) return;

        if (_tenantContext?.HasTenant != true)
        {
            throw new InvalidOperationException(
                $"'{typeof(T).Name}' is multi-tenant, but no tenant is set for this operation, so the "
                + "row would belong to no tenant. Ensure the request pipeline resolves a tenant: "
                + "app.UseMiddleware<TenantContextMiddleware>() reads it from the caller's token "
                + "(a 'tenant_id' or 'tenantId' claim), so issue tokens that carry one. Behind a "
                + "gateway that establishes the tenant itself, opt in to the header with "
                + "services.Configure<TenantContextOptions>(o => o.TrustCallerAssertedTenant = true). "
                + "Outside a request, set one explicitly via ITenantContext.SetTenantId before writing.");
        }

        tenanted.TenantId = _tenantContext.TenantId!;
    }

    /// <summary>
    /// The version that was read, as the update paths recorded it before applying the caller's changes.
    /// </summary>
    /// <remarks>
    /// Absent or non-<c>int</c> reads as zero, which no stored row carries — an insert writes 1 — so a
    /// check built on it matches nothing and the write is refused rather than applied blind.
    /// </remarks>
    public static int StoredVersion(IReadOnlyDictionary<string, object?> oldValues)
        => oldValues.TryGetValue("Version", out var ver) && ver is int verInt ? verInt : 0;

    /// <summary>
    /// The optimistic-concurrency filter: this row, still at the version we read, and still ours.
    /// </summary>
    public FilterDefinition<T> OccFilter(ObjectId id, int expectedVersion)
        => _accessPolicy.ScopeToOwner(_accessPolicy.ScopeToTenant(Builders<T>.Filter.And(
            Builders<T>.Filter.Eq(e => e.Id, id),
            Builders<T>.Filter.Eq(e => e.Version, expectedVersion)
        )));

    /// <summary>
    /// The version check alone, without tenant or owner scoping — what the bulk paths have always used.
    /// </summary>
    /// <remarks>
    /// Named apart from <see cref="OccFilter"/> so that choosing it is deliberate. Both bulk callers
    /// have already loaded their rows through a scoped read, so the id in hand is one the caller may
    /// write; see the class remarks for why this is depth that is missing rather than a hole.
    /// </remarks>
    public static FilterDefinition<T> UnscopedOccFilter(ObjectId id, int expectedVersion)
        => Builders<T>.Filter.And(
            Builders<T>.Filter.Eq(e => e.Id, id),
            Builders<T>.Filter.Eq(e => e.Version, expectedVersion)
        );

    /// <summary>
    /// Turns a replace that matched nothing into the right exception, by asking why it matched nothing.
    /// </summary>
    /// <remarks>
    /// A zero-match replace has two causes that a caller must be able to tell apart: the row moved on
    /// (someone else wrote it, so the version no longer matches) or the row is not there at all. The
    /// filter cannot distinguish them, so this re-queries by id alone. Doing nothing when the replace
    /// did match is the common path and costs no round trip.
    /// </remarks>
    /// <param name="displayId">
    /// The id as the caller named it, for the not-found message. <c>UpdateByObjectIdAsync</c> takes an
    /// <c>object</c> and echoes back what was passed rather than the parsed form.
    /// </param>
    public async Task ThrowOnConcurrencyConflictAsync(
        ReplaceOneResult result,
        ObjectId id,
        object displayId,
        WriteOperation operation,
        IClientSessionHandle? session,
        CancellationToken ct)
    {
        if (result.MatchedCount != 0) return;

        var existsFilter = Builders<T>.Filter.Eq(e => e.Id, id);
        long existsCount = session != null
            ? await _collection.CountDocumentsAsync(session, existsFilter, cancellationToken: ct)
            : await _collection.CountDocumentsAsync(existsFilter, cancellationToken: ct);

        if (existsCount > 0)
        {
            var qualifier = operation == WriteOperation.Restoration ? " during restoration" : string.Empty;
            throw new ConcurrencyException(id.ToString(), CollectionName,
                $"Optimistic concurrency check failed{qualifier}. Document with ID '{id}' was modified by another operation.");
        }

        var noun = operation == WriteOperation.Restoration ? "restoration" : "update";
        throw new KeyNotFoundException($"Entity with ID {displayId} not found or modified during {noun}.");
    }

    /// <summary>
    /// Refuses a bulk write in which any row's version check failed.
    /// </summary>
    /// <remarks>
    /// A bulk write reports one matched count for the batch, so a shortfall names no particular row.
    /// The whole batch is reported as conflicted, which is the only honest answer available here.
    /// </remarks>
    public void ThrowOnBulkConcurrencyConflict(BulkWriteResult<T> result, int expectedCount)
    {
        if (result.MatchedCount >= expectedCount) return;

        throw new ConcurrencyException("multiple-bulk-records", CollectionName,
            "Optimistic concurrency check failed. Some documents were modified by another transaction during bulk write.");
    }
}
