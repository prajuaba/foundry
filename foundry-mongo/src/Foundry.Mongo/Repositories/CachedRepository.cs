using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Foundry.Core.Entities;
using Foundry.Core.Paging;
using Foundry.Core.Search;
using Microsoft.Extensions.Caching.Memory;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Foundry.Mongo.Repositories;

/// <summary>
/// A transparent decorator that implements a cache-aside pattern on top of an existing Repository.
/// Intercepts GetByIdAsync to serve records from memory cache and invalidates them automatically on updates/deletes.
/// </summary>
public sealed class CachedRepository<T> : IRepository<T> where T : class, IEntity<ObjectId>
{
    private readonly Repository<T> _inner;
    private readonly IMemoryCache _cache;
    private readonly TimeSpan _defaultTtl;

    public string CollectionName => _inner.CollectionName;
    public IMongoCollection<T> Collection => _inner.Collection;

    /// <inheritdoc />
    public IQueryable<T> Query() => _inner.Query();

    public int MaxDepthCap
    {
        get => _inner.MaxDepthCap;
        set => _inner.MaxDepthCap = value;
    }

    private readonly Foundry.Core.User.ICurrentUserContext? _userContext;
    private readonly Foundry.Core.Tenant.ITenantContext? _tenantContext;

    /// <remarks>
    /// The two context dependencies are what make an entry belong to a caller rather than to a
    /// record; see <see cref="GetCacheKey"/>. They are optional so a repository composed outside a
    /// request — a background worker, a test — still constructs; such a caller fingerprints as
    /// anonymous and shares entries only with other callers holding nothing.
    /// </remarks>
    public CachedRepository(
        Repository<T> inner,
        IMemoryCache cache,
        CachedRepositoryOptions? options = null,
        Foundry.Core.User.ICurrentUserContext? userContext = null,
        Foundry.Core.Tenant.ITenantContext? tenantContext = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _defaultTtl = options?.DefaultTtl ?? TimeSpan.FromMinutes(5);
        _userContext = userContext;
        _tenantContext = tenantContext;
    }

    /// <summary>
    /// The cache key for one record <em>as one caller may see it</em>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This was <c>collection:id</c> — the identity of the record, not of the value stored against
    /// it. <c>Repository&lt;T&gt;.GetByIdAsync</c> returns a row that has already been through the
    /// tenant filter, the owner filter and per-category masking, so what gets cached is one
    /// caller's view, and every later caller asking for that id was served it.
    /// </para>
    /// <para>
    /// Reproduced against a live API: tenant <c>globex</c> read a project belonging to tenant
    /// <c>acme</c> by id and got 200, because <c>acme</c> had fetched it first. A direct read
    /// walking straight past the tenant filter is the worst shape this defect could take. In the
    /// same run a caller holding <c>view:contact</c> read a masked phone number out of an entry a
    /// caller with no scopes had populated; the inverse — an entry populated by a privileged caller
    /// and then served unmasked to everyone — is the same bug pointed the other way.
    /// </para>
    /// </remarks>
    private string GetCacheKey(object id)
        => $"foundrymongo:cache:{CollectionName}:{id}:{Fingerprint()}";

    private string Fingerprint()
        => Foundry.Core.Security.CallerViewFingerprint.Compute(
            _tenantContext?.TenantId, _userContext?.User);

    /// <summary>
    /// Tokens cancelled to evict every caller's copy of one record at once.
    /// </summary>
    /// <remarks>
    /// Per-caller keys mean a write can no longer evict by recomputing one key: whoever updates a
    /// record is generally not the only one holding a cached copy, and the others would keep
    /// serving the old value for the rest of the TTL. Entries are therefore tied to a change token
    /// per record, and invalidation cancels it, dropping every variant regardless of who cached it.
    /// </remarks>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, CancellationTokenSource>
        RecordTokens = new();

    private static string RecordTokenKey(string collection, object id) => collection + ":" + id;

    public async Task<T?> GetByIdAsync(object id, IClientSessionHandle? session = null, CancellationToken ct = default)
    {
        var key = GetCacheKey(id);
        if (_cache.TryGetValue<T>(key, out var cached))
        {
            return cached;
        }

        var result = await _inner.GetByIdAsync(id, session, ct);
        if (result != null)
        {
            var cts = RecordTokens.GetOrAdd(RecordTokenKey(CollectionName, id), _ => new CancellationTokenSource());

            // Raced with an invalidation that already cancelled this token: cache nothing, rather
            // than store an entry no future eviction will reach.
            if (!cts.IsCancellationRequested)
            {
                var entryOptions = new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = _defaultTtl };
                entryOptions.AddExpirationToken(
                    new Microsoft.Extensions.Primitives.CancellationChangeToken(cts.Token));
                _cache.Set(key, result, entryOptions);
            }
        }

        return result;
    }

    private void Invalidate(object id)
    {
        if (RecordTokens.TryRemove(RecordTokenKey(CollectionName, id), out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    public async Task InsertAsync(T entity, IClientSessionHandle? session = null, CancellationToken ct = default)
    {
        await _inner.InsertAsync(entity, session, ct);
    }

    public async Task BulkInsertAsync(IEnumerable<T> entities, IClientSessionHandle? session = null, CancellationToken ct = default)
    {
        await _inner.BulkInsertAsync(entities, session, ct);
    }

    public async Task UpdateAsync(T entity, IClientSessionHandle? session = null, CancellationToken ct = default)
    {
        Invalidate(entity.Id);
        await _inner.UpdateAsync(entity, session, ct);
    }

    public async Task UpdateByObjectIdAsync(object id, Func<T, T> updateSelector, string operatorId, IClientSessionHandle? session = null, CancellationToken ct = default)
    {
        Invalidate(id);
        await _inner.UpdateByObjectIdAsync(id, updateSelector, operatorId, session, ct);
    }

    public async Task BulkUpdateAsync(IEnumerable<T> entities, IClientSessionHandle? session = null, CancellationToken ct = default)
    {
        foreach (var entity in entities) Invalidate(entity.Id);
        await _inner.BulkUpdateAsync(entities, session, ct);
    }

    public async Task<IReadOnlyList<UpdateResult>> BulkUpdateManyAsync(Expression<Func<T, bool>> filter, Func<T, T> updateSelector, IClientSessionHandle? session = null, CancellationToken ct = default)
    {
        // Query matching records to invalidate cache keys prior to bulk update execution
        var matched = await _inner.FindManyAsync(filter, limit: 10000, session: session, ct: ct);
        foreach (var entity in matched) Invalidate(entity.Id);

        return await _inner.BulkUpdateManyAsync(filter, updateSelector, session, ct);
    }

    public async Task DeleteByObjectIdAsync(object id, string operatorId, IClientSessionHandle? session = null, CancellationToken ct = default)
    {
        Invalidate(id);
        await _inner.DeleteByObjectIdAsync(id, operatorId, session, ct);
    }

    public async Task DeleteAsync(ObjectId id, IClientSessionHandle? session = null, CancellationToken ct = default)
    {
        Invalidate(id);
        await _inner.DeleteAsync(id, session, ct);
    }

    public async Task RestoreDeletedAsync(ObjectId id, IClientSessionHandle? session = null, CancellationToken ct = default)
    {
        Invalidate(id);
        await _inner.RestoreDeletedAsync(id, session, ct);
    }

    public async Task<T> RestoreVersionAsync(object id, int version, IClientSessionHandle? session = null, CancellationToken ct = default)
    {
        Invalidate(id);
        return await _inner.RestoreVersionAsync(id, version, session, ct);
    }

    // ─── Direct Delegations ───────────────────────────────────────────────

    public Task<IReadOnlyList<T>> FindManyAsync(Expression<Func<T, bool>>? filter = null, string? sortBy = null, SortOrder sortOrder = SortOrder.Descending, int limit = 100, IClientSessionHandle? session = null, CancellationToken ct = default)
        => _inner.FindManyAsync(filter, sortBy, sortOrder, limit, session, ct);

    public Task<long> CountAsync(Expression<Func<T, bool>>? filter = null, IClientSessionHandle? session = null, CancellationToken ct = default)
        => _inner.CountAsync(filter, session, ct);

    public Task<PagedResult<T>> GetPagedAsync(PagedRequest request, Expression<Func<T, bool>>? filter = null, IClientSessionHandle? session = null, CancellationToken ct = default)
        => _inner.GetPagedAsync(request, filter, session, ct);

    public Task<IReadOnlyList<T>> GetPagedItemsAsync(PagedRequest request, Expression<Func<T, bool>>? filter = null, IClientSessionHandle? session = null, CancellationToken ct = default)
        => _inner.GetPagedItemsAsync(request, filter, session, ct);

    public Task<IReadOnlyList<T>> FindByCriteriaAsync(SearchCriterion[] criteria, IClientSessionHandle? session = null, CancellationToken ct = default)
        => _inner.FindByCriteriaAsync(criteria, session, ct);

    public Task<PagedResult<T>> SearchPagedAsync(SearchCriterion[] criteria, PagedRequest pageRequest, IClientSessionHandle? session = null, CancellationToken ct = default)
        => _inner.SearchPagedAsync(criteria, pageRequest, session, ct);

    public Task<PagedResult<UnifiedSearchResult>> CrossCollectionSearchAsync(CrossCollectionSearchRequest request, IClientSessionHandle? session = null, CancellationToken ct = default)
        => _inner.CrossCollectionSearchAsync(request, session, ct);

    public Task CreateIndexesAsync(CancellationToken ct = default)
        => _inner.CreateIndexesAsync(ct);

    public Task<IReadOnlyList<EntityRevision>> GetRevisionsAsync(object id, IClientSessionHandle? session = null, CancellationToken ct = default)
        => _inner.GetRevisionsAsync(id, session, ct);

    public Task<EntityRevision?> GetRevisionByVersionAsync(object id, int version, IClientSessionHandle? session = null, CancellationToken ct = default)
        => _inner.GetRevisionByVersionAsync(id, version, session, ct);

    public T MaskSensitiveFields(T entity)
        => _inner.MaskSensitiveFields(entity);

    public Task<IReadOnlyList<TResult>> AggregateAsync<TResult>(PipelineDefinition<T, TResult> pipeline, IClientSessionHandle? session = null, CancellationToken ct = default)
        => _inner.AggregateAsync(pipeline, session, ct);
}
