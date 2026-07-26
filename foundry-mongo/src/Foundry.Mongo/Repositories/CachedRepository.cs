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

    public int MaxDepthCap
    {
        get => _inner.MaxDepthCap;
        set => _inner.MaxDepthCap = value;
    }

    public CachedRepository(Repository<T> inner, IMemoryCache cache, CachedRepositoryOptions? options = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _defaultTtl = options?.DefaultTtl ?? TimeSpan.FromMinutes(5);
    }

    private string GetCacheKey(object id) => $"foundrymongo:cache:{CollectionName}:{id}";

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
            _cache.Set(key, result, _defaultTtl);
        }

        return result;
    }

    private void Invalidate(object id)
    {
        var key = GetCacheKey(id);
        _cache.Remove(key);
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
