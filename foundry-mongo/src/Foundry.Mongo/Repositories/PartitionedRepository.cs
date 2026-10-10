using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Foundry.Core.Audit;
using Foundry.Core.Attributes;
using Foundry.Core.Entities;
using Foundry.Core.Paging;
using Foundry.Core.Search;
using Foundry.Core.Security;
using Foundry.Core.User;
using Humanizer;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using MongoDB.Driver.Linq;

namespace Foundry.Mongo.Repositories;

/// <summary>
/// A repository implementation that automatically partitions data into Hot (active), Deleted,
/// and Cold (year-based archives) collections.
/// </summary>
public sealed class PartitionedRepository<T> : IRepository<T> where T : class, IEntity<ObjectId>
{
    private readonly IMongoDatabase _db;
    private readonly IAuditSink? _auditSink;
    private readonly ICurrentUserContext? _userContext;
    private readonly IEncryptionProvider? _encryptionProvider;

    private readonly Repository<T> _activeRepository;
    private readonly Repository<T> _deletedRepository;
    private readonly ConcurrentDictionary<int, Repository<T>> _archiveRepositories = new();
    private readonly int _thresholdYears;

    /// <summary>
    /// The ambient tenant, retained so archive repositories are scoped like the active one.
    /// </summary>
    /// <remarks>
    /// This was accepted by the constructor, handed to the active and deleted repositories, and then
    /// dropped. Archive repositories are created lazily per year, and had nothing to pass — so a row
    /// left tenant isolation the moment it aged past the archive threshold, and any tenant could read
    /// any other tenant's archived records by id. Isolation must not depend on how old a record is.
    /// </remarks>
    private readonly Foundry.Core.Tenant.ITenantContext? _tenantContext;

    /// <summary>
    /// MongoDB configuration options, retained so archive repositories get the same settings.
    /// </summary>
    private readonly Foundry.Mongo.DependencyInjection.FoundryMongoOptions? _mongoOptions;

    public IMongoCollection<T> Collection => _activeRepository.Collection;

    /// <inheritdoc />
    /// <remarks>
    /// Every partition: the active collection, then each archive through <c>$unionWith</c>, each
    /// branch under the same read filters. What the caller composes on top -- filters, sorting,
    /// paging -- runs after the union, so it sees one collection. It used to be the active
    /// collection only, and an archived row was simply absent from every composed query.
    /// </remarks>
    public IQueryable<T> Query()
    {
        var query = _activeRepository.Query();
        foreach (var archive in GetArchiveCollectionNames())
        {
            query = query.AppendStage<T, T>(UnionWith(archive));
        }
        return query;
    }
    public string CollectionName => _activeRepository.CollectionName;
    public int MaxDepthCap { get => _activeRepository.MaxDepthCap; set => _activeRepository.MaxDepthCap = value; }

    public PartitionedRepository(
        IMongoDatabase db,
        IAuditSink? auditSink = null,
        ICurrentUserContext? userContext = null,
        IEncryptionProvider? encryptionProvider = null,
        Foundry.Core.Tenant.ITenantContext? tenantContext = null,
        Foundry.Mongo.DependencyInjection.FoundryMongoOptions? mongoOptions = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _auditSink = auditSink;
        _userContext = userContext;
        _encryptionProvider = encryptionProvider;
        _tenantContext = tenantContext;
        _mongoOptions = mongoOptions;

        var partitionedAttribute = typeof(T).GetCustomAttribute<PartitionedAttribute>();
        _thresholdYears = partitionedAttribute?.ArchiveThresholdYears ?? 2;

        var baseCollectionName = typeof(T).Name.Pluralize();
        _activeRepository = new Repository<T>(db, auditSink, userContext, encryptionProvider, baseCollectionName, tenantContext, mongoOptions);
        _deletedRepository = new Repository<T>(db, auditSink, userContext, encryptionProvider, $"{baseCollectionName}_Deleted", tenantContext, mongoOptions);
    }

    private ObjectId ConvertId(object id)
    {
        return id is ObjectId oid ? oid : ObjectId.Parse(id.ToString());
    }

    private bool IsInArchive(int year)
    {
        return (DateTime.UtcNow.Year - year) >= _thresholdYears;
    }

    private Repository<T> GetArchiveRepository(int year)
    {
        return _archiveRepositories.GetOrAdd(year, y =>
        {
            var baseCollectionName = typeof(T).Name.Pluralize();
            var archiveCollectionName = $"{baseCollectionName}_{y}";
            return new Repository<T>(
                _db, _auditSink, _userContext, _encryptionProvider, archiveCollectionName, _tenantContext, _mongoOptions);
        });
    }

    /// <summary>
    /// The partition holding <paramref name="id"/>. An id past the threshold belongs in its year's
    /// archive, but stays in the active collection until the archival worker has moved it, so the
    /// archive is checked rather than assumed.
    /// </summary>
    private async Task<Repository<T>> LocateAsync(ObjectId id, IClientSessionHandle? session, CancellationToken ct)
    {
        var year = id.CreationTime.Year;
        if (!IsInArchive(year)) return _activeRepository;

        var archive = GetArchiveRepository(year);
        var filter = Builders<T>.Filter.Eq(e => e.Id, id);
        var options = new CountOptions { Limit = 1 };
        var found = session != null
            ? await archive.Collection.CountDocumentsAsync(session, filter, options, ct)
            : await archive.Collection.CountDocumentsAsync(filter, options, ct);
        return found > 0 ? archive : _activeRepository;
    }

    /// <summary>The active collection, then every archive that exists, newest year first.</summary>
    private async Task<IReadOnlyList<Repository<T>>> PartitionsAsync(CancellationToken ct)
    {
        var partitions = new List<Repository<T>> { _activeRepository };
        foreach (var name in (await GetArchiveCollectionNamesAsync(ct)).OrderByDescending(n => n, StringComparer.Ordinal))
        {
            partitions.Add(GetArchiveRepository(int.Parse(name[(name.LastIndexOf('_') + 1)..])));
        }
        return partitions;
    }

    /// <summary>
    /// Rows from several partitions in one order. Each partition has already sorted and limited its
    /// own rows by the same key, so the first N of the merge are the first N overall.
    /// </summary>
    private static IEnumerable<T> Merge(IEnumerable<T> rows, string? fieldName, SortOrder order)
    {
        if (string.IsNullOrWhiteSpace(fieldName)) return rows;

        var property = fieldName == "_id"
            ? typeof(T).GetProperty(nameof(IEntity<ObjectId>.Id))
            : typeof(T).GetProperty(fieldName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (property == null) return rows;

        // Ordinal for strings, as MongoDB compares them without a collation.
        var comparer = Comparer<object?>.Create((a, b) => a is string x && b is string y
            ? string.CompareOrdinal(x, y)
            : Comparer<object?>.Default.Compare(a, b));
        return order == SortOrder.Ascending
            ? rows.OrderBy(r => property.GetValue(r), comparer)
            : rows.OrderByDescending(r => property.GetValue(r), comparer);
    }

    /// <summary>The sort an offset page is read in: the request's, or insertion order.</summary>
    private static (string Field, SortOrder Order) PageSort(PagedRequest request)
        => request.SortBy != null
            ? (request.SortBy.FieldName, request.SortBy.Order)
            : (nameof(IEntity<ObjectId>.Id), SortOrder.Ascending);

    /// <summary>A <c>$unionWith</c> stage reading <paramref name="archive"/> under the active collection's read filters.</summary>
    private PipelineStageDefinition<T, T> UnionWith(string archive)
    {
        var serializer = BsonSerializer.LookupSerializer<T>();
        var match = _activeRepository.ReadFilter(Builders<T>.Filter.Empty)
            .Render(new RenderArgs<T>(serializer, BsonSerializer.SerializerRegistry));
        return new BsonDocument("$unionWith", new BsonDocument
        {
            ["coll"] = archive,
            ["pipeline"] = new BsonArray { new BsonDocument("$match", match) }
        });
    }

    private static void SetProperty(object obj, string propertyName, object? value)
    {
        var type = obj.GetType();
        var prop = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
        if (prop != null)
        {
            var backingField = type.GetField($"<{propertyName}>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
            if (backingField != null)
            {
                backingField.SetValue(obj, value);
                return;
            }
            prop.SetValue(obj, value);
        }
    }

    public async Task<T?> GetByIdAsync(object id, IClientSessionHandle? session = null, CancellationToken ct = default)
    {
        var objectId = ConvertId(id);
        var repo = await LocateAsync(objectId, session, ct);
        if (repo != _activeRepository)
        {
            return await repo.GetByIdAsync(objectId, session, ct);
        }

        var activeResult = await _activeRepository.GetByIdAsync(objectId, session, ct);
        if (activeResult != null) return activeResult;

        return await _deletedRepository.GetByIdAsync(objectId, session, ct);
    }

    public async Task InsertAsync(T entity, IClientSessionHandle? session = null, CancellationToken ct = default)
    {
        await _activeRepository.InsertAsync(entity, session, ct);
    }

    public async Task BulkInsertAsync(IEnumerable<T> entities, IClientSessionHandle? session = null, CancellationToken ct = default)
    {
        await _activeRepository.BulkInsertAsync(entities, session, ct);
    }

    public async Task<IReadOnlyList<T>> FindManyAsync(Expression<Func<T, bool>>? filter = null, string? sortBy = null, SortOrder sortOrder = SortOrder.Descending, int limit = 100, IClientSessionHandle? session = null, CancellationToken ct = default)
    {
        // Every partition, whatever the filter names. Rows are archived by the year in their id,
        // so a filter on any other field says nothing about where they are; routing on the filter
        // read only the active collection for all of them.
        var rows = new List<T>();
        foreach (var repo in await PartitionsAsync(ct))
        {
            rows.AddRange(await repo.FindManyAsync(filter, sortBy, sortOrder, limit, session, ct));
        }
        return Merge(rows, sortBy, sortOrder).Take(limit).ToList();
    }

    public async Task<long> CountAsync(Expression<Func<T, bool>>? filter = null, IClientSessionHandle? session = null, CancellationToken ct = default)
    {
        long total = 0;
        foreach (var repo in await PartitionsAsync(ct))
        {
            total += await repo.CountAsync(filter, session, ct);
        }
        return total;
    }

    public async Task<PagedResult<T>> GetPagedAsync(PagedRequest request, Expression<Func<T, bool>>? filter = null, IClientSessionHandle? session = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var partitions = await PartitionsAsync(ct);
        if (partitions.Count == 1)
        {
            return await _activeRepository.GetPagedAsync(request, filter, session, ct);
        }

        if (request.CursorInfo != null)
        {
            // Each partition seeks past the same cursor, so the next page is the first PageSize of
            // their merge, and there is a next page if any partition had more.
            var rows = new List<T>();
            var more = false;
            foreach (var repo in partitions)
            {
                var page = await repo.GetPagedAsync(request, filter, session, ct);
                rows.AddRange(page.Items);
                more |= page.NextCursor != null;
            }

            var merged = Merge(rows, request.CursorInfo.FieldName, request.CursorInfo.Order).ToList();
            var items = merged.Take(request.PageSize).ToList();
            more |= merged.Count > request.PageSize;
            if (!more || items.Count == 0)
            {
                return new PagedResult<T> { Items = items, TotalRecords = items.Count, PageNumber = request.PageNumber, PageSize = request.PageSize };
            }

            var next = CursorSeekInfo.FromValue(items[^1], request.CursorInfo.FieldName, request.CursorInfo.Order);
            return PagedResult<T>.WithCursor(items, items.Count + 1, request.PageNumber, request.PageSize, next);
        }

        return PagedResult<T>.From(
            await OffsetPageAsync(partitions, request, filter, session, ct),
            await CountAsync(filter, session, ct),
            request.PageNumber,
            request.PageSize);
    }

    public async Task<IReadOnlyList<T>> GetPagedItemsAsync(PagedRequest request, Expression<Func<T, bool>>? filter = null, IClientSessionHandle? session = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var partitions = await PartitionsAsync(ct);
        if (partitions.Count == 1 || request.CursorInfo != null)
        {
            return partitions.Count == 1
                ? await _activeRepository.GetPagedItemsAsync(request, filter, session, ct)
                : (await GetPagedAsync(request, filter, session, ct)).Items;
        }

        return await OffsetPageAsync(partitions, request, filter, session, ct);
    }

    /// <summary>
    /// One offset page across partitions. Page N of the whole is not page N of each partition, so
    /// each is read from its start to the page's end, in the page's order, and the merge is cut.
    /// </summary>
    private static async Task<IReadOnlyList<T>> OffsetPageAsync(
        IReadOnlyList<Repository<T>> partitions, PagedRequest request, Expression<Func<T, bool>>? filter,
        IClientSessionHandle? session, CancellationToken ct)
    {
        OffsetPaginationHelper.ValidatePageNumber(request.PageNumber);
        OffsetPaginationHelper.ValidatePageSize(request.PageSize);
        var depth = OffsetPaginationHelper.CheckDepth(request.PageNumber, request.PageSize, request.MaxDepthCap);
        if (depth.IsExceeded)
        {
            throw new ArgumentException($"Offset pagination depth ({depth.TotalDepthUsed}) exceeds configured MaxDepthCap ({request.MaxDepthCap}). Use cursor-based pagination instead.");
        }

        var (field, order) = PageSort(request);
        var end = request.PageNumber * request.PageSize;
        var rows = new List<T>();
        foreach (var repo in partitions)
        {
            rows.AddRange(await repo.FindManyAsync(filter, field, order, end, session, ct));
        }
        return Merge(rows, field, order).Skip(end - request.PageSize).Take(request.PageSize).ToList();
    }

    public async Task UpdateByObjectIdAsync(object id, Func<T, T> updateSelector, string operatorId, IClientSessionHandle? session = null, CancellationToken ct = default)
    {
        var objectId = ConvertId(id);
        var repo = await LocateAsync(objectId, session, ct);
        await repo.UpdateByObjectIdAsync(objectId, updateSelector, operatorId, session, ct);
    }

    public async Task UpdateAsync(T entity, IClientSessionHandle? session = null, CancellationToken ct = default)
    {
        var objectId = ConvertId(entity.Id);
        var repo = await LocateAsync(objectId, session, ct);
        await repo.UpdateAsync(entity, session, ct);
    }

    public async Task<IReadOnlyList<UpdateResult>> BulkUpdateManyAsync(Expression<Func<T, bool>> filter, Func<T, T> updateSelector, IClientSessionHandle? session = null, CancellationToken ct = default)
    {
        var results = new List<UpdateResult>();
        foreach (var repo in await PartitionsAsync(ct))
        {
            var res = await repo.BulkUpdateManyAsync(filter, updateSelector, session, ct);
            results.AddRange(res);
        }
        return results;
    }

    public async Task BulkUpdateAsync(IEnumerable<T> entities, IClientSessionHandle? session = null, CancellationToken ct = default)
    {
        var located = new List<(Repository<T> Repo, T Entity)>();
        foreach (var entity in entities)
        {
            located.Add((await LocateAsync(ConvertId(entity.Id), session, ct), entity));
        }
        foreach (var group in located.GroupBy(x => x.Repo))
        {
            await group.Key.BulkUpdateAsync(group.Select(x => x.Entity), session, ct);
        }
    }

    public async Task DeleteByObjectIdAsync(object id, string operatorId, IClientSessionHandle? session = null, CancellationToken ct = default)
    {
        var objectId = ConvertId(id);
        var sourceRepo = await LocateAsync(objectId, session, ct);

        var entity = await sourceRepo.GetByIdAsync(objectId, session, ct);
        if (entity == null) return;

        if (entity is ISoftDelete)
        {
            var actualSession = session ?? await _db.Client.StartSessionAsync(cancellationToken: ct);
            var isLocalSession = session == null;
            var moved = false;

            try
            {
                if (isLocalSession) actualSession.StartTransaction();

                // The lookup above is the access check; what moves is the row as stored. The entity
                // it returned is decrypted and masked for the caller, and inserting it through the
                // repository stamped a new CreatedAtUtc and Version 1, audited an insert, and -- for
                // a caller who may not read a masked field -- would store the mask in place of the
                // value. A range check does not apply either: the row is not a new value.
                var filter = Builders<T>.Filter.Eq(e => e.Id, objectId);
                var stored = await sourceRepo.Collection.Find(actualSession, filter).FirstOrDefaultAsync(ct);
                if (stored != null)
                {
                    var now = DateTime.UtcNow;
                    SetProperty(stored, "IsDeleted", true);
                    SetProperty(stored, "DeletedAt", now);
                    stored.UpdatedAtUtc = now;
                    stored.Version += 1;

                    await _deletedRepository.Collection.InsertOneAsync(actualSession, stored, cancellationToken: ct);
                    await sourceRepo.Collection.DeleteOneAsync(actualSession, filter, cancellationToken: ct);
                    moved = true;
                }

                if (isLocalSession) await actualSession.CommitTransactionAsync(ct);
            }
            catch
            {
                if (isLocalSession) await actualSession.AbortTransactionAsync(ct);
                throw;
            }
            finally
            {
                if (isLocalSession) actualSession.Dispose();
            }

            // The move bypasses the source repository's own delete, which is where every other
            // soft delete is audited, so it is audited here -- unless the row went between the
            // lookup and the move, when there was nothing to delete.
            if (moved && _auditSink != null)
            {
                var entry = AuditLogEntry.ForSoftDelete(
                    operatorId,
                    typeof(T).FullName ?? typeof(T).Name,
                    objectId.ToString(),
                    sourceRepo.CollectionName);
                await _auditSink.WriteAsync(entry with { TenantId = _tenantContext?.TenantId }, ct);
            }
        }
        else
        {
            await sourceRepo.DeleteByObjectIdAsync(objectId, operatorId, session, ct);
        }
    }

    public async Task DeleteAsync(ObjectId id, IClientSessionHandle? session = null, CancellationToken ct = default)
    {
        await DeleteByObjectIdAsync(id, "System", session, ct);
    }

    public async Task<IReadOnlyList<T>> FindByCriteriaAsync(SearchCriterion[] criteria, IClientSessionHandle? session = null, CancellationToken ct = default)
    {
        var rows = new List<T>();
        foreach (var repo in await PartitionsAsync(ct))
        {
            rows.AddRange(await repo.FindByCriteriaAsync(criteria, session, ct));
        }
        return rows;
    }

    /// <remarks>
    /// The active repository's alone. A cross-collection search reads the collections of the entity
    /// types it is asked for, not this repository's, so asking each archive as well returned every
    /// result once per archive.
    /// </remarks>
    public Task<PagedResult<UnifiedSearchResult>> CrossCollectionSearchAsync(CrossCollectionSearchRequest request, IClientSessionHandle? session = null, CancellationToken ct = default)
        => _activeRepository.CrossCollectionSearchAsync(request, session, ct);

    public Task<PagedResult<T>> SearchPagedAsync(SearchCriterion[] criteria, PagedRequest pageRequest, IClientSessionHandle? session = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        return GetPagedAsync(pageRequest, _activeRepository.SearchExpression(criteria), session, ct);
    }

    public async Task CreateIndexesAsync(CancellationToken ct = default)
    {
        await _activeRepository.CreateIndexesAsync(ct);
        await _deletedRepository.CreateIndexesAsync(ct);
        foreach (var repo in (await PartitionsAsync(ct)).Skip(1))
        {
            await repo.CreateIndexesAsync(ct);
        }
    }

    public async Task<IReadOnlyList<EntityRevision>> GetRevisionsAsync(object id, IClientSessionHandle? session = null, CancellationToken ct = default)
    {
        var objectId = ConvertId(id);
        var repo = await LocateAsync(objectId, session, ct);
        var revisions = await repo.GetRevisionsAsync(objectId, session, ct);

        if (!revisions.Any())
        {
            revisions = await _deletedRepository.GetRevisionsAsync(objectId, session, ct);
        }

        return revisions;
    }

    public async Task<EntityRevision?> GetRevisionByVersionAsync(object id, int version, IClientSessionHandle? session = null, CancellationToken ct = default)
    {
        var objectId = ConvertId(id);
        var repo = await LocateAsync(objectId, session, ct);
        var revision = await repo.GetRevisionByVersionAsync(objectId, version, session, ct);

        if (revision == null)
        {
            revision = await _deletedRepository.GetRevisionByVersionAsync(objectId, version, session, ct);
        }

        return revision;
    }

    public async Task<T> RestoreVersionAsync(object id, int version, IClientSessionHandle? session = null, CancellationToken ct = default)
    {
        var objectId = ConvertId(id);
        var repo = await LocateAsync(objectId, session, ct);
        return await repo.RestoreVersionAsync(objectId, version, session, ct);
    }

    /// <inheritdoc />
    public bool HasProtectedProperties => _activeRepository.HasProtectedProperties;

    /// <inheritdoc />
    public IReadOnlyList<T> ProtectForRead(IReadOnlyList<T> entities)
        => _activeRepository.ProtectForRead(entities);

    public T MaskSensitiveFields(T entity)
    {
        return _activeRepository.MaskSensitiveFields(entity);
    }

    public async Task RestoreDeletedAsync(ObjectId id, IClientSessionHandle? session = null, CancellationToken ct = default)
    {
        // Scoped to the caller's tenant and ownership, as every other way to the row is. It was
        // found by id alone, so any tenant could restore any other tenant's deleted row.
        var filter = Builders<T>.Filter.Eq(e => e.Id, id);
        var scoped = _deletedRepository.CallerScope(filter);
        var actualSession = session ?? await _db.Client.StartSessionAsync(cancellationToken: ct);
        var isLocalSession = session == null;
        var restored = false;

        try
        {
            if (isLocalSession) actualSession.StartTransaction();

            // Moved as stored, the reverse of DeleteByObjectIdAsync. It went through the target's
            // insert, which encrypted values that were already ciphertext, and stamped the row as
            // new -- CreatedAtUtc of the restore, Version 1.
            var stored = await _deletedRepository.Collection.Find(actualSession, scoped).FirstOrDefaultAsync(ct);
            if (stored != null)
            {
                SetProperty(stored, "IsDeleted", false);
                SetProperty(stored, "DeletedAt", null as DateTime?);
                stored.UpdatedAtUtc = DateTime.UtcNow;
                stored.Version += 1;

                var year = id.CreationTime.Year;
                var target = IsInArchive(year) ? GetArchiveRepository(year) : _activeRepository;
                await target.Collection.InsertOneAsync(actualSession, stored, cancellationToken: ct);
                await _deletedRepository.Collection.DeleteOneAsync(actualSession, filter, cancellationToken: ct);
                restored = true;
            }

            if (isLocalSession) await actualSession.CommitTransactionAsync(ct);
        }
        catch
        {
            if (isLocalSession) await actualSession.AbortTransactionAsync(ct);
            throw;
        }
        finally
        {
            if (isLocalSession) actualSession.Dispose();
        }

        if (restored && _auditSink != null)
        {
            var entry = AuditLogEntry.ForRestore(
                _userContext?.OperatorId ?? "system",
                typeof(T).FullName ?? typeof(T).Name,
                id.ToString(),
                CollectionName);
            await _auditSink.WriteAsync(entry with { TenantId = _tenantContext?.TenantId }, ct);
        }
    }

    private ListCollectionNamesOptions ArchiveNameFilter() => new()
    {
        Filter = new BsonDocument("name", new BsonRegularExpression($"^{typeof(T).Name.Pluralize()}_\\d{{4}}$"))
    };

    private List<string> GetArchiveCollectionNames()
        => _db.ListCollectionNames(ArchiveNameFilter()).ToList();

    private async Task<List<string>> GetArchiveCollectionNamesAsync(CancellationToken ct)
        => await (await _db.ListCollectionNamesAsync(ArchiveNameFilter(), ct)).ToListAsync(ct);

    public async Task<IReadOnlyList<TResult>> AggregateAsync<TResult>(PipelineDefinition<T, TResult> pipeline, IClientSessionHandle? session = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        var serializer = BsonSerializer.LookupSerializer<T>();
        var args = new RenderArgs<T>(serializer, BsonSerializer.SerializerRegistry);

        // The read filters first on the active collection and inside every archive branch, then the
        // caller's stages over the union -- the same rule Repository.AggregateAsync follows. The
        // archives were unioned in unfiltered and nothing was applied to the active collection, so
        // the caller's pipeline ran over every tenant's rows.
        var stages = new List<BsonDocument>
        {
            new("$match", _activeRepository.ReadFilter(Builders<T>.Filter.Empty).Render(args))
        };
        foreach (var archive in await GetArchiveCollectionNamesAsync(ct))
        {
            stages.Add(((PipelineStageDefinition<T, T>)UnionWith(archive)).Render(new RenderArgs<T>(serializer, BsonSerializer.SerializerRegistry)).Document);
        }
        stages.AddRange(pipeline.Render(args).Documents);

        var finalPipeline = PipelineDefinition<T, TResult>.Create(stages);
        var collection = Collection.WithReadPreference(ReadPreference.SecondaryPreferred);

        var cursor = session != null
            ? await collection.AggregateAsync(session, finalPipeline, cancellationToken: ct)
            : await collection.AggregateAsync(finalPipeline, cancellationToken: ct);

        return await cursor.ToListAsync(ct);
    }
}
