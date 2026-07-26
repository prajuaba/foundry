using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using Foundry.Core.Attributes;
using Foundry.Core.Audit;
using Foundry.Core.User;
using Foundry.Mongo.Infrastructure;
using Foundry.Core.Entities;
using Foundry.Core.Paging;
using Foundry.Core.Search;
using Foundry.Core.Security;
using Humanizer;
using Foundry.Mongo.Infrastructure.Search;
using Foundry.Mongo.Services;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace Foundry.Mongo.Repositories;

/// <summary>
/// Default repository implementation providing full CRUD operations with soft-delete via ISoftDelete, cursor/offset pagination, dynamic expression search, and audit trail.
/// Uses MongoDB.Driver v3 native async APIs exclusively — zero blocking calls or synchronous overloads.
/// </summary>
public sealed class Repository<T> : IRepository<T> where T : class, IEntity<ObjectId>
{
    private readonly IMongoCollection<T> _collection;
    private readonly IAuditSink? _auditSink;
    private readonly ICurrentUserContext? _userContext;
    private readonly IEncryptionProvider? _encryptionProvider;
    private readonly Foundry.Core.Tenant.ITenantContext? _tenantContext;

    private readonly EntityEncryptionService<T> _encryptionService;
    private readonly EntityAuditService<T> _auditService;
    private readonly EntityVersioningService<T> _versioningService;
    private readonly EntityIndexManager<T> _indexManager;

    public string CollectionName => _collection.CollectionNamespace.CollectionName;

    public int MaxDepthCap { get; set; } = 10_000;

    public IMongoCollection<T> Collection => _collection;

    public Repository(
        IMongoDatabase db,
        IAuditSink? auditSink = null,
        ICurrentUserContext? userContext = null,
        IEncryptionProvider? encryptionProvider = null,
        string? collectionName = null,
        Foundry.Core.Tenant.ITenantContext? tenantContext = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        _auditSink = auditSink;
        _userContext = userContext;
        _tenantContext = tenantContext;

        // Validate: if entity has properties requiring encryption, an IEncryptionProvider must be registered
        var hasEncryptedProperties = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Any(p => p.GetCustomAttribute<SensitiveDataAttribute>()?.Protection == ProtectionType.Encrypt);

        if (hasEncryptedProperties && encryptionProvider == null)
        {
            throw new InvalidOperationException(
                $"Entity type '{typeof(T).Name}' has properties marked with [SensitiveData(Protection = Encrypt)] " +
                $"but no IEncryptionProvider is registered. Register an IEncryptionProvider in the DI container " +
                $"or remove the Encrypt protection from the entity properties.");
        }

        _encryptionProvider = encryptionProvider;

        _encryptionService = new EntityEncryptionService<T>(encryptionProvider);
        _auditService = new EntityAuditService<T>(auditSink, userContext);
        _versioningService = new EntityVersioningService<T>(db);

        var actualCollectionName = collectionName ?? typeof(T).Name.Pluralize();
        _collection = db.GetCollection<T>(actualCollectionName);
        _indexManager = new EntityIndexManager<T>(_collection);
    }

    public async Task<T?> GetByIdAsync(object id, IClientSessionHandle? session = null, CancellationToken ct = default)
    {
        var objectId = ConvertToObjectId(id);
        var filter = Builders<T>.Filter.Eq(e => e.Id, objectId);
        filter = ApplySoftDeleteFilter(filter);
        
        var findOptions = new FindOptions<T> { Limit = 1 };
        var cursor = session != null
            ? await _collection.FindAsync(session, filter, findOptions, ct)
            : await _collection.FindAsync(filter, findOptions, ct);

        var entity = await cursor.FirstOrDefaultAsync(ct);
        DecryptEntity(entity);
        
        if (entity != null)
        {
            await AuditReadAsync(entity.Id.ToString(), ct);
        }

        return entity;
    }

    // ─── Create operations ────────────────────────────────────────────────

    public async Task InsertAsync(T entity, IClientSessionHandle? session = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entity);

        var now = DateTime.UtcNow;
        entity.CreatedAtUtc = now;
        entity.UpdatedAtUtc = now;
        entity.Version = 1;

        var encrypted = EncryptEntityForWrite(entity);

        if (session != null)
        {
            await _collection.InsertOneAsync(session, encrypted, null, ct);
        }
        else
        {
            await _collection.InsertOneAsync(encrypted, null, ct);
        }

        var operatorId = GetCurrentOperatorId();

        // Historical Revision snapshot (stores encrypted state for security)
        if (entity is IVersionable)
        {
            var historyCollectionName = CollectionName + "_History";
            var historyCollection = _collection.Database.GetCollection<EntityRevision>(historyCollectionName);
            var revision = new EntityRevision
            {
                EntityId = entity.Id.ToString(),
                Version = entity.Version,
                Data = encrypted.ToBsonDocument(),
                ChangedBy = operatorId,
                Action = "Insert"
            };
            if (session != null)
                await historyCollection.InsertOneAsync(session, revision, null, ct);
            else
                await historyCollection.InsertOneAsync(revision, null, ct);
        }

        if (_auditSink != null)
        {
            var entry = AuditLogEntry.ForInsert(
                operatorId,
                typeof(T).FullName ?? typeof(T).Name,
                entity.Id.ToString(),
                CollectionName);
            await _auditSink.WriteAsync(entry, ct);
        }
    }

    public async Task BulkInsertAsync(IEnumerable<T> entities, IClientSessionHandle? session = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entities);
        var list = entities.ToList();
        if (list.Count == 0) return;

        var now = DateTime.UtcNow;
        foreach (var entity in list)
        {
            entity.CreatedAtUtc = now;
            entity.UpdatedAtUtc = now;
            entity.Version = 1;
        }

        var encryptedList = list.Select(EncryptEntityForWrite).ToList();

        if (session != null)
        {
            await _collection.InsertManyAsync(session, encryptedList, null, ct);
        }
        else
        {
            await _collection.InsertManyAsync(encryptedList, null, ct);
        }

        var operatorId = GetCurrentOperatorId();

        // Historical Revision snapshot (stores encrypted state)
        if (typeof(IVersionable).IsAssignableFrom(typeof(T)))
        {
            var historyCollectionName = CollectionName + "_History";
            var historyCollection = _collection.Database.GetCollection<EntityRevision>(historyCollectionName);
            var revisions = encryptedList.Select(entity => new EntityRevision
            {
                EntityId = entity.Id.ToString(),
                Version = entity.Version,
                Data = entity.ToBsonDocument(),
                ChangedBy = operatorId,
                Action = "Insert"
            }).ToList();

            if (session != null)
                await historyCollection.InsertManyAsync(session, revisions, null, ct);
            else
                await historyCollection.InsertManyAsync(revisions, null, ct);
        }

        if (_auditSink != null)
        {
            var auditEntries = list.Select(entity => AuditLogEntry.ForInsert(
                operatorId,
                typeof(T).FullName ?? typeof(T).Name,
                entity.Id.ToString(),
                CollectionName)).ToList();

            await _auditSink.WriteManyAsync(auditEntries, ct);
        }
    }

    // ─── Read operations ──────────────────────────────────────────────────

    public async Task<IReadOnlyList<T>> FindManyAsync(
        Expression<Func<T, bool>>? filter = null,
        string? sortBy = null,
        SortOrder sortOrder = SortOrder.Descending,
        int limit = 100,
        IClientSessionHandle? session = null,
        CancellationToken ct = default)
    {
        var finalFilter = ApplySoftDeleteFilter(filter);
        var findOptions = new FindOptions<T> { Limit = limit };

        if (!string.IsNullOrWhiteSpace(sortBy))
        {
            var sortBuilder = Builders<T>.Sort;
            var sortDefinition = sortOrder == SortOrder.Ascending
                ? sortBuilder.Ascending(sortBy)
                : sortBuilder.Descending(sortBy);
            findOptions.Sort = sortDefinition;
        }

        var cursor = session != null
            ? await _collection.FindAsync(session, finalFilter, findOptions, ct)
            : await _collection.FindAsync(finalFilter, findOptions, ct);

        var items = await cursor.ToListAsync(ct);
        foreach (var item in items) DecryptEntity(item);
        
        await AuditReadsAsync(items.Select(e => e.Id.ToString()), ct);

        return items;
    }

    public async Task<long> CountAsync(Expression<Func<T, bool>>? filter = null, IClientSessionHandle? session = null, CancellationToken ct = default)
    {
        var finalFilter = ApplySoftDeleteFilter(filter);
        return session != null
            ? await _collection.CountDocumentsAsync(session, finalFilter, cancellationToken: ct)
            : await _collection.CountDocumentsAsync(finalFilter, cancellationToken: ct);
    }

    // ─── Paginations ──────────────────────────────────────────────────────

    public async Task<PagedResult<T>> GetPagedAsync(
        PagedRequest request,
        Expression<Func<T, bool>>? filter = null,
        IClientSessionHandle? session = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.CursorInfo != null)
        {
            var seekFilter = SeekPaginationHelper.BuildSeekFilter<T>(
                request.CursorInfo.FieldName,
                request.CursorInfo.Value,
                request.CursorInfo.Order == SortOrder.Ascending);

            var mongoFilter = filter != null ? Builders<T>.Filter.Where(filter) : Builders<T>.Filter.Empty;
            mongoFilter = ApplySoftDeleteFilter(mongoFilter);
            mongoFilter = Builders<T>.Filter.And(mongoFilter, Builders<T>.Filter.Where(seekFilter));

            var sortDef = BuildSortDefinition(request);
            var findOptions = new FindOptions<T>
            {
                Limit = request.PageSize + 1,
                Sort = sortDef
            };

            var cursor = session != null
                ? await _collection.FindAsync(session, mongoFilter, findOptions, ct)
                : await _collection.FindAsync(mongoFilter, findOptions, ct);

            var items = await cursor.ToListAsync(ct);
            foreach (var item in items) DecryptEntity(item);

            await AuditReadsAsync(items.Select(e => e.Id.ToString()), ct);

            var hasNextPage = items.Count > request.PageSize;
            var pageItems = hasNextPage ? items.Take(request.PageSize).ToList() : items;

            CursorSeekInfo? nextCursor = null;
            if (hasNextPage && pageItems.Count > 0)
            {
                var lastItem = pageItems[^1];
                nextCursor = CursorSeekInfo.FromValue(lastItem, request.CursorInfo.FieldName, request.CursorInfo.Order);
            }

            if (nextCursor != null)
            {
                return PagedResult<T>.WithCursor(pageItems, pageItems.Count + 1, request.PageNumber, request.PageSize, nextCursor);
            }
            else
            {
                return new PagedResult<T>
                {
                    Items = pageItems,
                    TotalRecords = pageItems.Count,
                    PageNumber = request.PageNumber,
                    PageSize = request.PageSize,
                    NextCursor = null
                };
            }
        }
        else
        {
            OffsetPaginationHelper.ValidatePageNumber(request.PageNumber);
            OffsetPaginationHelper.ValidatePageSize(request.PageSize);

            var depthCheck = OffsetPaginationHelper.CheckDepth(request.PageNumber, request.PageSize, request.MaxDepthCap);
            if (depthCheck.IsExceeded)
            {
                throw new ArgumentException($"Offset pagination depth ({depthCheck.TotalDepthUsed}) exceeds configured MaxDepthCap ({request.MaxDepthCap}). Use cursor-based pagination instead.");
            }

            var mongoFilter = filter != null ? Builders<T>.Filter.Where(filter) : Builders<T>.Filter.Empty;
            mongoFilter = ApplySoftDeleteFilter(mongoFilter);

            var totalRecords = session != null
                ? await _collection.CountDocumentsAsync(session, mongoFilter, cancellationToken: ct)
                : await _collection.CountDocumentsAsync(mongoFilter, cancellationToken: ct);

            var (skip, take) = OffsetPaginationHelper.GetSkipTakeValues(request);
            var sortDef = BuildSortDefinition(request);
            var findOptions = new FindOptions<T>
            {
                Skip = (int)skip,
                Limit = take,
                Sort = sortDef
            };

            var cursor = session != null
                ? await _collection.FindAsync(session, mongoFilter, findOptions, ct)
                : await _collection.FindAsync(mongoFilter, findOptions, ct);

            var items = await cursor.ToListAsync(ct);
            foreach (var item in items) DecryptEntity(item);

            await AuditReadsAsync(items.Select(e => e.Id.ToString()), ct);

            return PagedResult<T>.From(items, totalRecords, request.PageNumber, request.PageSize);
        }
    }

    public async Task<IReadOnlyList<T>> GetPagedItemsAsync(
        PagedRequest request,
        Expression<Func<T, bool>>? filter = null,
        IClientSessionHandle? session = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var mongoFilter = filter != null ? Builders<T>.Filter.Where(filter) : Builders<T>.Filter.Empty;
        mongoFilter = ApplySoftDeleteFilter(mongoFilter);

        var sortDef = BuildSortDefinition(request);

        if (request.CursorInfo != null)
        {
            var seekFilter = SeekPaginationHelper.BuildSeekFilter<T>(
                request.CursorInfo.FieldName,
                request.CursorInfo.Value,
                request.CursorInfo.Order == SortOrder.Ascending);

            mongoFilter = Builders<T>.Filter.And(mongoFilter, Builders<T>.Filter.Where(seekFilter));

            var findOptions = new FindOptions<T>
            {
                Limit = request.PageSize,
                Sort = sortDef
            };

            var cursor = session != null
                ? await _collection.FindAsync(session, mongoFilter, findOptions, ct)
                : await _collection.FindAsync(mongoFilter, findOptions, ct);

            var items = await cursor.ToListAsync(ct);
            foreach (var item in items) DecryptEntity(item);
            await AuditReadsAsync(items.Select(e => e.Id.ToString()), ct);
            return items;
        }
        else
        {
            OffsetPaginationHelper.ValidatePageNumber(request.PageNumber);
            OffsetPaginationHelper.ValidatePageSize(request.PageSize);

            var depthCheck = OffsetPaginationHelper.CheckDepth(request.PageNumber, request.PageSize, request.MaxDepthCap);
            if (depthCheck.IsExceeded)
            {
                throw new ArgumentException($"Offset pagination depth ({depthCheck.TotalDepthUsed}) exceeds configured MaxDepthCap ({request.MaxDepthCap}). Use cursor-based pagination instead.");
            }

            var (skip, take) = OffsetPaginationHelper.GetSkipTakeValues(request);
            var findOptions = new FindOptions<T>
            {
                Skip = (int)skip,
                Limit = take,
                Sort = sortDef
            };

            var cursor = session != null
                ? await _collection.FindAsync(session, mongoFilter, findOptions, ct)
                : await _collection.FindAsync(mongoFilter, findOptions, ct);

            var items = await cursor.ToListAsync(ct);
            foreach (var item in items) DecryptEntity(item);
            await AuditReadsAsync(items.Select(e => e.Id.ToString()), ct);
            return items;
        }
    }

    // ─── Update operations ────────────────────────────────────────────────

    public async Task UpdateByObjectIdAsync(
        object id,
        Func<T, T> updateSelector,
        string operatorId,
        IClientSessionHandle? session = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(updateSelector);

        var objectId = ConvertToObjectId(id);
        var filter = Builders<T>.Filter.Eq(e => e.Id, objectId);

        var existingCursor = session != null
            ? await _collection.FindAsync(session, filter, new FindOptions<T> { Limit = 1 }, ct)
            : await _collection.FindAsync(filter, new FindOptions<T> { Limit = 1 }, ct);

        var entity = await existingCursor.FirstOrDefaultAsync(ct);
        if (entity == null)
            throw new KeyNotFoundException($"Entity with ID {id} not found in collection '{CollectionName}'");

        DecryptEntity(entity);

        var oldValues = new Dictionary<string, object?>();
        var properties = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        foreach (var prop in properties)
        {
            if (prop.CanRead) oldValues[prop.Name] = prop.GetValue(entity);
        }

        var entityAfter = updateSelector(entity);
        if (entityAfter == null)
            throw new InvalidOperationException("Update selector returned a null entity.");

        if (oldValues.TryGetValue("CreatedAtUtc", out var cat) && cat is DateTime catTime)
        {
            entityAfter.CreatedAtUtc = catTime;
        }

        entityAfter.UpdatedAtUtc = DateTime.UtcNow;
        var oldVersion = oldValues.TryGetValue("Version", out var ver) && ver is int verInt ? verInt : 0;
        entityAfter.Version = oldVersion + 1;

        bool isSoftDeletedNow = false;
        if (entityAfter is ISoftDelete softEntity)
        {
            var oldIsDeleted = oldValues.TryGetValue("IsDeleted", out var oid) && oid is bool oidBool && oidBool;
            if (softEntity.IsDeleted && !oldIsDeleted)
            {
                isSoftDeletedNow = true;
                SetProperty(entityAfter, "DeletedAt", DateTime.UtcNow);
            }
        }

        // Optimistic Concurrency Control
        var occFilter = Builders<T>.Filter.And(
            Builders<T>.Filter.Eq(e => e.Id, objectId),
            Builders<T>.Filter.Eq(e => e.Version, oldVersion)
        );

        var encrypted = EncryptEntityForWrite(entityAfter);

        ReplaceOneResult replaceResult;
        if (session != null)
        {
            replaceResult = await _collection.ReplaceOneAsync(session, occFilter, encrypted, new ReplaceOptions { IsUpsert = false }, ct);
        }
        else
        {
            replaceResult = await _collection.ReplaceOneAsync(occFilter, encrypted, new ReplaceOptions { IsUpsert = false }, ct);
        }

        if (replaceResult.MatchedCount == 0)
        {
            var existsFilter = Builders<T>.Filter.Eq(e => e.Id, objectId);
            long existsCount = session != null
                ? await _collection.CountDocumentsAsync(session, existsFilter, cancellationToken: ct)
                : await _collection.CountDocumentsAsync(existsFilter, cancellationToken: ct);

            if (existsCount > 0)
            {
                throw new ConcurrencyException(objectId.ToString(), CollectionName,
                    $"Optimistic concurrency check failed. Document with ID '{objectId}' was modified by another operation.");
            }
            throw new KeyNotFoundException($"Entity with ID {id} not found or modified during update.");
        }

        // Historical Revision snapshot (stores encrypted state)
        if (entityAfter is IVersionable)
        {
            var historyCollectionName = CollectionName + "_History";
            var historyCollection = _collection.Database.GetCollection<EntityRevision>(historyCollectionName);
            var revision = new EntityRevision
            {
                EntityId = entityAfter.Id.ToString(),
                Version = entityAfter.Version,
                Data = encrypted.ToBsonDocument(),
                ChangedBy = operatorId,
                Action = isSoftDeletedNow ? "SoftDelete" : "Update"
            };
            if (session != null)
                await historyCollection.InsertOneAsync(session, revision, null, ct);
            else
                await historyCollection.InsertOneAsync(revision, null, ct);
        }

        if (_auditSink != null)
        {
            AuditLogEntry entry;
            if (isSoftDeletedNow)
            {
                entry = AuditLogEntry.ForSoftDelete(
                    operatorId,
                    typeof(T).FullName ?? typeof(T).Name,
                    entityAfter.Id.ToString(),
                    CollectionName);
            }
            else
            {
                var diffs = new List<PropertyDiff>();
                foreach (var prop in properties)
                {
                    if (!prop.CanRead || prop.Name == "Id") continue;

                    var oldVal = oldValues.TryGetValue(prop.Name, out var v) ? v : null;
                    var newVal = prop.GetValue(entityAfter);

                    if (!object.Equals(oldVal, newVal))
                    {
                        diffs.Add(new PropertyDiff
                        {
                            PropertyName = prop.Name,
                            OldValue = GetDiffValue(prop, oldVal),
                            NewValue = GetDiffValue(prop, newVal)
                        });
                    }
                }

                entry = AuditLogEntry.ForUpdate(
                    operatorId,
                    typeof(T).FullName ?? typeof(T).Name,
                    entityAfter.Id.ToString(),
                    CollectionName,
                    diffs);
            }

            await _auditSink.WriteAsync(entry, ct);
        }
    }

    public async Task UpdateAsync(T entity, IClientSessionHandle? session = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        var operatorId = GetCurrentOperatorId();

        var oldVersion = entity.Version;
        var filter = Builders<T>.Filter.Eq(e => e.Id, entity.Id);

        var existingCursor = session != null
            ? await _collection.FindAsync(session, filter, new FindOptions<T> { Limit = 1 }, ct)
            : await _collection.FindAsync(filter, new FindOptions<T> { Limit = 1 }, ct);

        var existing = await existingCursor.FirstOrDefaultAsync(ct);
        if (existing == null)
            throw new KeyNotFoundException($"Entity with ID {entity.Id} not found in collection '{CollectionName}'");

        DecryptEntity(existing);

        var oldValues = new Dictionary<string, object?>();
        var properties = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        foreach (var prop in properties)
        {
            if (prop.CanRead) oldValues[prop.Name] = prop.GetValue(existing);
        }

        if (oldValues.TryGetValue("CreatedAtUtc", out var cat) && cat is DateTime catTime)
        {
            entity.CreatedAtUtc = catTime;
        }
        entity.UpdatedAtUtc = DateTime.UtcNow;
        entity.Version = oldVersion + 1;

        bool isSoftDeletedNow = false;
        if (entity is ISoftDelete softEntity)
        {
            var oldIsDeleted = oldValues.TryGetValue("IsDeleted", out var oid) && oid is bool oidBool && oidBool;
            if (softEntity.IsDeleted && !oldIsDeleted)
            {
                isSoftDeletedNow = true;
                SetProperty(entity, "DeletedAt", DateTime.UtcNow);
            }
        }

        // Optimistic Concurrency Control
        var occFilter = Builders<T>.Filter.And(
            Builders<T>.Filter.Eq(e => e.Id, entity.Id),
            Builders<T>.Filter.Eq(e => e.Version, oldVersion)
        );

        var encrypted = EncryptEntityForWrite(entity);

        ReplaceOneResult replaceResult;
        if (session != null)
        {
            replaceResult = await _collection.ReplaceOneAsync(session, occFilter, encrypted, new ReplaceOptions { IsUpsert = false }, ct);
        }
        else
        {
            replaceResult = await _collection.ReplaceOneAsync(occFilter, encrypted, new ReplaceOptions { IsUpsert = false }, ct);
        }

        if (replaceResult.MatchedCount == 0)
        {
            var existsFilter = Builders<T>.Filter.Eq(e => e.Id, entity.Id);
            long existsCount = session != null
                ? await _collection.CountDocumentsAsync(session, existsFilter, cancellationToken: ct)
                : await _collection.CountDocumentsAsync(existsFilter, cancellationToken: ct);

            if (existsCount > 0)
            {
                throw new ConcurrencyException(entity.Id.ToString(), CollectionName,
                    $"Optimistic concurrency check failed. Document with ID '{entity.Id}' was modified by another operation.");
            }
            throw new KeyNotFoundException($"Entity with ID {entity.Id} not found or modified during update.");
        }

        // Historical Revision snapshot (stores encrypted state)
        if (entity is IVersionable)
        {
            var historyCollectionName = CollectionName + "_History";
            var historyCollection = _collection.Database.GetCollection<EntityRevision>(historyCollectionName);
            var revision = new EntityRevision
            {
                EntityId = entity.Id.ToString(),
                Version = entity.Version,
                Data = encrypted.ToBsonDocument(),
                ChangedBy = operatorId,
                Action = isSoftDeletedNow ? "SoftDelete" : "Update"
            };
            if (session != null)
                await historyCollection.InsertOneAsync(session, revision, null, ct);
            else
                await historyCollection.InsertOneAsync(revision, null, ct);
        }

        if (_auditSink != null)
        {
            AuditLogEntry entry;
            if (isSoftDeletedNow)
            {
                entry = AuditLogEntry.ForSoftDelete(
                    operatorId,
                    typeof(T).FullName ?? typeof(T).Name,
                    entity.Id.ToString(),
                    CollectionName);
            }
            else
            {
                var diffs = new List<PropertyDiff>();
                foreach (var prop in properties)
                {
                    if (!prop.CanRead || prop.Name == "Id") continue;

                    var oldVal = oldValues.TryGetValue(prop.Name, out var v) ? v : null;
                    var newVal = prop.GetValue(entity);

                    if (!object.Equals(oldVal, newVal))
                    {
                        diffs.Add(new PropertyDiff
                        {
                            PropertyName = prop.Name,
                            OldValue = GetDiffValue(prop, oldVal),
                            NewValue = GetDiffValue(prop, newVal)
                        });
                    }
                }

                entry = AuditLogEntry.ForUpdate(
                    operatorId,
                    typeof(T).FullName ?? typeof(T).Name,
                    entity.Id.ToString(),
                    CollectionName,
                    diffs);
            }

            await _auditSink.WriteAsync(entry, ct);
        }
    }

    public async Task<IReadOnlyList<UpdateResult>> BulkUpdateManyAsync(
        Expression<Func<T, bool>> filter,
        Func<T, T> updateSelector,
        IClientSessionHandle? session = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(updateSelector);

        var finalFilter = ApplySoftDeleteFilter(filter);
        
        var findOptions = new FindOptions<T>();
        var entitiesCursor = session != null
            ? await _collection.FindAsync(session, finalFilter, findOptions, ct)
            : await _collection.FindAsync(finalFilter, findOptions, ct);

        var entities = await entitiesCursor.ToListAsync(ct);

        if (entities.Count == 0)
        {
            return Array.Empty<UpdateResult>();
        }

        var writeModels = new List<WriteModel<T>>();
        var auditEntries = new List<AuditLogEntry>();
        var revisions = new List<EntityRevision>();
        var results = new List<UpdateResult>();

        foreach (var entity in entities)
        {
            DecryptEntity(entity);

            var oldValues = new Dictionary<string, object?>();
            var properties = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance);
            foreach (var prop in properties)
            {
                if (prop.CanRead) oldValues[prop.Name] = prop.GetValue(entity);
            }

            var entityAfter = updateSelector(entity);
            if (entityAfter == null) continue;

            if (oldValues.TryGetValue("CreatedAtUtc", out var cat) && cat is DateTime catTime)
            {
                entityAfter.CreatedAtUtc = catTime;
            }

            entityAfter.UpdatedAtUtc = DateTime.UtcNow;
            var oldVersion = oldValues.TryGetValue("Version", out var ver) && ver is int verInt ? verInt : 0;
            entityAfter.Version = oldVersion + 1;

            bool isSoftDeletedNow = false;
            if (entityAfter is ISoftDelete softEntity)
            {
                var oldIsDeleted = oldValues.TryGetValue("IsDeleted", out var oid) && oid is bool oidBool && oidBool;
                if (softEntity.IsDeleted && !oldIsDeleted)
                {
                    isSoftDeletedNow = true;
                    SetProperty(entityAfter, "DeletedAt", DateTime.UtcNow);
                }
            }

            var encrypted = EncryptEntityForWrite(entityAfter);

            // OCC check per entity replacement in bulk writes
            var replaceFilter = Builders<T>.Filter.And(
                Builders<T>.Filter.Eq(e => e.Id, entityAfter.Id),
                Builders<T>.Filter.Eq(e => e.Version, oldVersion)
            );
            writeModels.Add(new ReplaceOneModel<T>(replaceFilter, encrypted) { IsUpsert = false });

            if (entityAfter is IVersionable)
            {
                revisions.Add(new EntityRevision
                {
                    EntityId = entityAfter.Id.ToString(),
                    Version = entityAfter.Version,
                    Data = encrypted.ToBsonDocument(),
                    ChangedBy = GetCurrentOperatorId(),
                    Action = isSoftDeletedNow ? "SoftDelete" : "Update"
                });
            }

            if (_auditSink != null)
            {
                AuditLogEntry entry;
                if (isSoftDeletedNow)
                {
                    entry = AuditLogEntry.ForSoftDelete(
                        GetCurrentOperatorId(),
                        typeof(T).FullName ?? typeof(T).Name,
                        entityAfter.Id.ToString(),
                        CollectionName);
                }
                else
                {
                    var diffs = new List<PropertyDiff>();
                    foreach (var prop in properties)
                    {
                        if (!prop.CanRead || prop.Name == "Id") continue;

                        var oldVal = oldValues.TryGetValue(prop.Name, out var v) ? v : null;
                        var newVal = prop.GetValue(entityAfter);

                        if (!object.Equals(oldVal, newVal))
                        {
                            diffs.Add(new PropertyDiff
                            {
                                PropertyName = prop.Name,
                                OldValue = GetDiffValue(prop, oldVal),
                                NewValue = GetDiffValue(prop, newVal)
                            });
                        }
                    }
                    entry = AuditLogEntry.ForUpdate(
                        GetCurrentOperatorId(),
                        typeof(T).FullName ?? typeof(T).Name,
                        entityAfter.Id.ToString(),
                        CollectionName,
                        diffs);
                }
                auditEntries.Add(entry);
            }
        }

        if (writeModels.Count > 0)
        {
            var bulkResult = session != null
                ? await _collection.BulkWriteAsync(session, writeModels, null, ct)
                : await _collection.BulkWriteAsync(writeModels, null, ct);

            // If we did not match all documents, it implies concurrency conflict in a multi-client context
            if (bulkResult.MatchedCount < writeModels.Count)
            {
                throw new ConcurrencyException("multiple-bulk-records", CollectionName,
                    "Optimistic concurrency check failed. Some documents were modified by another transaction during bulk write.");
            }

            var updateResult = new UpdateResult.Acknowledged(bulkResult.MatchedCount, bulkResult.ModifiedCount, null);
            results.Add(updateResult);

            if (revisions.Count > 0)
            {
                var historyCollectionName = CollectionName + "_History";
                var historyCollection = _collection.Database.GetCollection<EntityRevision>(historyCollectionName);
                if (session != null)
                    await historyCollection.InsertManyAsync(session, revisions, null, ct);
                else
                    await historyCollection.InsertManyAsync(revisions, null, ct);
            }

            if (_auditSink != null && auditEntries.Count > 0)
            {
                await _auditSink.WriteManyAsync(auditEntries, ct);
            }
        }

        return results;
    }

    public async Task BulkUpdateAsync(IEnumerable<T> entities, IClientSessionHandle? session = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entities);
        var list = entities.ToList();
        if (list.Count == 0) return;

        var writeModels = new List<WriteModel<T>>();
        var auditEntries = new List<AuditLogEntry>();
        var revisions = new List<EntityRevision>();
        var operatorId = GetCurrentOperatorId();

        foreach (var entity in list)
        {
            var filter = Builders<T>.Filter.Eq(e => e.Id, entity.Id);
            var findOptions = new FindOptions<T> { Limit = 1 };
            var existingCursor = session != null
                ? await _collection.FindAsync(session, filter, findOptions, ct)
                : await _collection.FindAsync(filter, findOptions, ct);

            var existing = await existingCursor.FirstOrDefaultAsync(ct);
            if (existing == null) continue;

            DecryptEntity(existing);

            var oldValues = new Dictionary<string, object?>();
            var properties = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance);
            foreach (var prop in properties)
            {
                if (prop.CanRead) oldValues[prop.Name] = prop.GetValue(existing);
            }

            if (oldValues.TryGetValue("CreatedAtUtc", out var cat) && cat is DateTime catTime)
            {
                entity.CreatedAtUtc = catTime;
            }

            var oldVersion = oldValues.TryGetValue("Version", out var ver) && ver is int verInt ? verInt : 0;
            entity.UpdatedAtUtc = DateTime.UtcNow;
            entity.Version = oldVersion + 1;

            bool isSoftDeletedNow = false;
            if (entity is ISoftDelete softEntity)
            {
                var oldIsDeleted = oldValues.TryGetValue("IsDeleted", out var oid) && oid is bool oidBool && oidBool;
                if (softEntity.IsDeleted && !oldIsDeleted)
                {
                    isSoftDeletedNow = true;
                    SetProperty(entity, "DeletedAt", DateTime.UtcNow);
                }
            }

            var encrypted = EncryptEntityForWrite(entity);

            var replaceFilter = Builders<T>.Filter.And(
                Builders<T>.Filter.Eq(e => e.Id, entity.Id),
                Builders<T>.Filter.Eq(e => e.Version, oldVersion)
            );
            writeModels.Add(new ReplaceOneModel<T>(replaceFilter, encrypted) { IsUpsert = false });

            if (entity is IVersionable)
            {
                revisions.Add(new EntityRevision
                {
                    EntityId = entity.Id.ToString(),
                    Version = entity.Version,
                    Data = encrypted.ToBsonDocument(),
                    ChangedBy = operatorId,
                    Action = isSoftDeletedNow ? "SoftDelete" : "Update"
                });
            }

            if (_auditSink != null)
            {
                AuditLogEntry entry;
                if (isSoftDeletedNow)
                {
                    entry = AuditLogEntry.ForSoftDelete(
                        operatorId,
                        typeof(T).FullName ?? typeof(T).Name,
                        entity.Id.ToString(),
                        CollectionName);
                }
                else
                {
                    var diffs = new List<PropertyDiff>();
                    foreach (var prop in properties)
                    {
                        if (!prop.CanRead || prop.Name == "Id") continue;

                        var oldVal = oldValues.TryGetValue(prop.Name, out var v) ? v : null;
                        var newVal = prop.GetValue(entity);

                        if (!object.Equals(oldVal, newVal))
                        {
                            diffs.Add(new PropertyDiff
                            {
                                PropertyName = prop.Name,
                                OldValue = GetDiffValue(prop, oldVal),
                                NewValue = GetDiffValue(prop, newVal)
                            });
                        }
                    }
                    entry = AuditLogEntry.ForUpdate(
                        operatorId,
                        typeof(T).FullName ?? typeof(T).Name,
                        entity.Id.ToString(),
                        CollectionName,
                        diffs);
                }
                auditEntries.Add(entry);
            }
        }

        if (writeModels.Count > 0)
        {
            var bulkResult = session != null
                ? await _collection.BulkWriteAsync(session, writeModels, null, ct)
                : await _collection.BulkWriteAsync(writeModels, null, ct);

            if (bulkResult.MatchedCount < writeModels.Count)
            {
                throw new ConcurrencyException("multiple-bulk-records", CollectionName,
                    "Optimistic concurrency check failed. Some documents were modified by another transaction during bulk write.");
            }

            if (revisions.Count > 0)
            {
                var historyCollectionName = CollectionName + "_History";
                var historyCollection = _collection.Database.GetCollection<EntityRevision>(historyCollectionName);
                if (session != null)
                    await historyCollection.InsertManyAsync(session, revisions, null, ct);
                else
                    await historyCollection.InsertManyAsync(revisions, null, ct);
            }

            if (_auditSink != null && auditEntries.Count > 0)
            {
                await _auditSink.WriteManyAsync(auditEntries, ct);
            }
        }
    }

    // ─── Delete operations ────────────────────────────────────────────────

    public async Task DeleteByObjectIdAsync(
        object id,
        string operatorId,
        IClientSessionHandle? session = null,
        CancellationToken ct = default)
    {
        var objectId = ConvertToObjectId(id);
        var filter = Builders<T>.Filter.Eq(e => e.Id, objectId);

        var findOptions = new FindOptions<T> { Limit = 1 };
        var existingCursor = session != null
            ? await _collection.FindAsync(session, filter, findOptions, ct)
            : await _collection.FindAsync(filter, findOptions, ct);

        var entity = await existingCursor.FirstOrDefaultAsync(ct);
        if (entity == null) return;

        DecryptEntity(entity);

        if (entity is ISoftDelete)
        {
            var update = Builders<T>.Update
                .Set("IsDeleted", true)
                .Set("DeletedAt", DateTime.UtcNow)
                .Set("UpdatedAtUtc", DateTime.UtcNow)
                .Inc("Version", 1);

            if (session != null)
            {
                await _collection.UpdateOneAsync(session, filter, update, null, ct);
            }
            else
            {
                await _collection.UpdateOneAsync(filter, update, null, ct);
            }

            // Fetch the updated soft-deleted representation for version history
            var updatedCursor = session != null
                ? await _collection.FindAsync(session, filter, findOptions, ct)
                : await _collection.FindAsync(filter, findOptions, ct);
            var updatedEntity = await updatedCursor.FirstOrDefaultAsync(ct);

            if (updatedEntity != null && updatedEntity is IVersionable)
            {
                var historyCollectionName = CollectionName + "_History";
                var historyCollection = _collection.Database.GetCollection<EntityRevision>(historyCollectionName);
                var revision = new EntityRevision
                {
                    EntityId = updatedEntity.Id.ToString(),
                    Version = updatedEntity.Version,
                    Data = updatedEntity.ToBsonDocument(), // stores encrypted database state
                    ChangedBy = operatorId,
                    Action = "SoftDelete"
                };
                if (session != null)
                    await historyCollection.InsertOneAsync(session, revision, null, ct);
                else
                    await historyCollection.InsertOneAsync(revision, null, ct);
            }

            if (_auditSink != null)
            {
                var entry = AuditLogEntry.ForSoftDelete(
                    operatorId,
                    typeof(T).FullName ?? typeof(T).Name,
                    entity.Id.ToString(),
                    CollectionName);
                await _auditSink.WriteAsync(entry, ct);
            }
        }
        else
        {
            if (session != null)
            {
                await _collection.DeleteOneAsync(session, filter, null, ct);
            }
            else
            {
                await _collection.DeleteOneAsync(filter, null, ct);
            }

            if (entity is IVersionable)
            {
                var historyCollectionName = CollectionName + "_History";
                var historyCollection = _collection.Database.GetCollection<EntityRevision>(historyCollectionName);
                
                // For hard deletes, we preserve the last database-side (encrypted) BSON representation in history
                var encrypted = EncryptEntityForWrite(entity);
                var revision = new EntityRevision
                {
                    EntityId = entity.Id.ToString(),
                    Version = entity.Version + 1,
                    Data = encrypted.ToBsonDocument(),
                    ChangedBy = operatorId,
                    Action = "HardDelete"
                };
                if (session != null)
                    await historyCollection.InsertOneAsync(session, revision, null, ct);
                else
                    await historyCollection.InsertOneAsync(revision, null, ct);
            }

            if (_auditSink != null)
            {
                var entry = AuditLogEntry.ForHardDelete(
                    operatorId,
                    typeof(T).FullName ?? typeof(T).Name,
                    entity.Id.ToString(),
                    CollectionName);
                await _auditSink.WriteAsync(entry, ct);
            }
        }
    }

    public async Task DeleteAsync(ObjectId id, IClientSessionHandle? session = null, CancellationToken ct = default)
    {
        var operatorId = GetCurrentOperatorId();
        await DeleteByObjectIdAsync(id, operatorId, session, ct);
    }

    // ─── Search operations ────────────────────────────────────────────────

    public async Task<IReadOnlyList<T>> FindByCriteriaAsync(SearchCriterion[] criteria, IClientSessionHandle? session = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        var expression = BuildExpression(criteria);
        var finalFilter = ApplySoftDeleteFilter(expression);

        var cursor = session != null
            ? await _collection.FindAsync(session, finalFilter, null, ct)
            : await _collection.FindAsync(finalFilter, null, ct);

        var items = await cursor.ToListAsync(ct);
        foreach (var item in items) DecryptEntity(item);
        
        await AuditReadsAsync(items.Select(e => e.Id.ToString()), ct);

        return items;
    }

    public async Task<PagedResult<T>> SearchPagedAsync(
        SearchCriterion[] criteria,
        PagedRequest pageRequest,
        IClientSessionHandle? session = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        ArgumentNullException.ThrowIfNull(pageRequest);

        var expression = BuildExpression(criteria);
        return await GetPagedAsync(pageRequest, expression, session, ct);
    }

    // ─── Cross collection search ──────────────────────────────────────────

    public async Task<PagedResult<UnifiedSearchResult>> CrossCollectionSearchAsync(
        CrossCollectionSearchRequest request,
        IClientSessionHandle? session = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.EntityTypes == null || request.EntityTypes.Count == 0)
        {
            return PagedResult<UnifiedSearchResult>.Empty(
                request.Pagination?.PageNumber ?? 1,
                request.Pagination?.PageSize ?? 20);
        }

        var db = _collection.Database;
        var list = new List<(Type EntityType, string CollectionName)>();

        foreach (var entityType in request.EntityTypes)
        {
            var collectionName = request.CollectionToEntityTypeMap?.FirstOrDefault(x => x.Value == entityType).Key;
            if (string.IsNullOrEmpty(collectionName))
            {
                collectionName = entityType.Name.Pluralize();
            }
            list.Add((entityType, collectionName));
        }

        var first = list[0];
        var firstMatchDoc = BuildBsonFilter(request.Criteria);
        if (typeof(ISoftDelete).IsAssignableFrom(first.EntityType))
        {
            firstMatchDoc["IsDeleted"] = new BsonDocument("$ne", true);
        }

        var firstProjectDoc = new BsonDocument
        {
            { "_id", 0 },
            { "EntityId", new BsonDocument("$toString", "$_id") },
            { "CollectionsName", first.CollectionName },
            { "EntityType", first.EntityType.FullName ?? first.EntityType.Name },
            { "Properties", "$$ROOT" }
        };

        var unionStages = new List<BsonDocument>();
        for (int i = 1; i < list.Count; i++)
        {
            var other = list[i];
            var otherMatchDoc = BuildBsonFilter(request.Criteria);
            if (typeof(ISoftDelete).IsAssignableFrom(other.EntityType))
            {
                otherMatchDoc["IsDeleted"] = new BsonDocument("$ne", true);
            }

            var otherProjectDoc = new BsonDocument
            {
                { "_id", 0 },
                { "EntityId", new BsonDocument("$toString", "$_id") },
                { "CollectionsName", other.CollectionName },
                { "EntityType", other.EntityType.FullName ?? other.EntityType.Name },
                { "Properties", "$$ROOT" }
            };

            var unionStage = new BsonDocument("$unionWith", new BsonDocument
            {
                { "coll", other.CollectionName },
                { "pipeline", new BsonArray
                    {
                        new BsonDocument("$match", otherMatchDoc),
                        new BsonDocument("$project", otherProjectDoc)
                    }
                }
            });

            unionStages.Add(unionStage);
        }

        BsonDocument sortStage;
        if (request.Pagination?.SortBy != null)
        {
            var sortField = request.Pagination.SortBy.FieldName;
            if (!sortField.Equals("EntityId", StringComparison.OrdinalIgnoreCase) &&
                !sortField.Equals("CollectionsName", StringComparison.OrdinalIgnoreCase) &&
                !sortField.Equals("EntityType", StringComparison.OrdinalIgnoreCase))
            {
                sortField = "Properties." + sortField;
            }
            var sortOrder = request.Pagination.SortBy.Order == SortOrder.Ascending ? 1 : -1;
            sortStage = new BsonDocument("$sort", new BsonDocument(sortField, sortOrder));
        }
        else
        {
            sortStage = new BsonDocument("$sort", new BsonDocument("EntityId", -1));
        }

        var pageNumber = request.Pagination?.PageNumber ?? 1;
        var pageSize = request.Pagination?.PageSize ?? 20;
        var skip = Math.Max(0, (pageNumber - 1) * pageSize);

        var facetStage = new BsonDocument("$facet", new BsonDocument
        {
            { "metadata", new BsonArray { new BsonDocument("$count", "total") } },
            { "data", new BsonArray
                {
                    sortStage,
                    new BsonDocument("$skip", skip),
                    new BsonDocument("$limit", pageSize)
                }
            }
        });

        var mainPipeline = new List<BsonDocument>
        {
            new BsonDocument("$match", firstMatchDoc),
            new BsonDocument("$project", firstProjectDoc)
        };

        foreach (var unionStage in unionStages)
        {
            mainPipeline.Add(unionStage);
        }

        mainPipeline.Add(facetStage);

        var firstCollection = db.GetCollection<BsonDocument>(first.CollectionName);
        var pipelineDef = PipelineDefinition<BsonDocument, BsonDocument>.Create(mainPipeline);
        
        var aggregationCursor = session != null
            ? await firstCollection.AggregateAsync<BsonDocument>(session, pipelineDef, null, ct)
            : await firstCollection.AggregateAsync<BsonDocument>(pipelineDef, null, ct);

        var facetResultDoc = await aggregationCursor.FirstOrDefaultAsync(ct);

        long totalRecords = 0;
        if (facetResultDoc != null && facetResultDoc.TryGetValue("metadata", out var metadataVal) && metadataVal.IsBsonArray)
        {
            var metadataArray = metadataVal.AsBsonArray;
            if (metadataArray.Count > 0 && metadataArray[0].AsBsonDocument.TryGetValue("total", out var totalVal))
            {
                totalRecords = totalVal.BsonType switch
                {
                    BsonType.Int32 => totalVal.AsInt32,
                    BsonType.Int64 => totalVal.AsInt64,
                    _ => Convert.ToInt64(totalVal.ToString(), CultureInfo.InvariantCulture)
                };
            }
        }

        var items = new List<UnifiedSearchResult>();
        if (facetResultDoc != null && facetResultDoc.TryGetValue("data", out var dataVal) && dataVal.IsBsonArray)
        {
            foreach (var itemVal in dataVal.AsBsonArray)
            {
                var itemDoc = itemVal.AsBsonDocument;
                var entityId = itemDoc.GetValue("EntityId", string.Empty).ToString() ?? string.Empty;
                var collectionsName = itemDoc.GetValue("CollectionsName", string.Empty).ToString() ?? string.Empty;
                var entityTypeStr = itemDoc.GetValue("EntityType", string.Empty).ToString() ?? string.Empty;

                var properties = new Dictionary<string, object?>();
                if (itemDoc.TryGetValue("Properties", out var propsVal) && propsVal.IsBsonDocument)
                {
                    var propsDoc = propsVal.AsBsonDocument;
                    int propCount = 0;
                    foreach (var element in propsDoc)
                    {
                        if (request.MaxPropertyCount > 0 && propCount >= request.MaxPropertyCount)
                            break;

                        if (element.Name == "_id") continue;

                        properties[element.Name] = BsonTypeMapper.MapToDotNetValue(element.Value);
                        propCount++;
                    }
                }

                // If fields are encrypted in the properties dictionary, they will remain encrypted here since cross-collection is raw database projection.
                // Decrypting fields in heterogeneous query properties dictionary can be performed by looking up the type metadata:
                var matchingType = request.EntityTypes.FirstOrDefault(t => t.FullName == entityTypeStr || t.Name == entityTypeStr);
                if (matchingType != null)
                {
                    var matchingProps = matchingType.GetProperties(BindingFlags.Public | BindingFlags.Instance);
                    foreach (var p in matchingProps)
                    {
                        var sensitiveAttr = p.GetCustomAttribute<SensitiveDataAttribute>();
                        if (sensitiveAttr != null && sensitiveAttr.Protection == ProtectionType.Encrypt)
                        {
                            if (_encryptionProvider != null && properties.TryGetValue(p.Name, out var cipherVal) && cipherVal != null)
                            {
                                properties[p.Name] = _encryptionProvider.Decrypt(cipherVal.ToString() ?? string.Empty);
                            }
                        }
                    }
                }

                items.Add(new UnifiedSearchResult
                {
                    EntityId = entityId,
                    CollectionsName = collectionsName,
                    EntityType = entityTypeStr,
                    Properties = properties
                });
            }
        }

        return PagedResult<UnifiedSearchResult>.From(items, totalRecords, pageNumber, pageSize);
    }

    public async Task CreateIndexesAsync(CancellationToken ct = default)
    {
        await _indexManager.CreateIndexesAsync(ct);
    }

    // ─── Historical Versioning operations ─────────────────────────────────

    public async Task<IReadOnlyList<EntityRevision>> GetRevisionsAsync(object id, IClientSessionHandle? session = null, CancellationToken ct = default)
    {
        var objectIdStr = ConvertToObjectId(id).ToString();
        var historyCollectionName = CollectionName + "_History";
        var historyCollection = _collection.Database.GetCollection<EntityRevision>(historyCollectionName);

        var filter = Builders<EntityRevision>.Filter.Eq(r => r.EntityId, objectIdStr);
        var sort = Builders<EntityRevision>.Sort.Descending(r => r.Version);

        var findOptions = new FindOptions<EntityRevision> { Sort = sort };
        var cursor = session != null
            ? await historyCollection.FindAsync(session, filter, findOptions, ct)
            : await historyCollection.FindAsync(filter, findOptions, ct);

        var revisions = await cursor.ToListAsync(ct);
        if (revisions.Any())
        {
            await AuditReadAsync(objectIdStr, ct);
        }
        return revisions;
    }

    public async Task<EntityRevision?> GetRevisionByVersionAsync(object id, int version, IClientSessionHandle? session = null, CancellationToken ct = default)
    {
        var objectIdStr = ConvertToObjectId(id).ToString();
        var historyCollectionName = CollectionName + "_History";
        var historyCollection = _collection.Database.GetCollection<EntityRevision>(historyCollectionName);

        var filter = Builders<EntityRevision>.Filter.And(
            Builders<EntityRevision>.Filter.Eq(r => r.EntityId, objectIdStr),
            Builders<EntityRevision>.Filter.Eq(r => r.Version, version)
        );

        var findOptions = new FindOptions<EntityRevision> { Limit = 1 };
        var cursor = session != null
            ? await historyCollection.FindAsync(session, filter, findOptions, ct)
            : await historyCollection.FindAsync(filter, findOptions, ct);

        var revision = await cursor.FirstOrDefaultAsync(ct);
        if (revision != null)
        {
            await AuditReadAsync(objectIdStr, ct);
        }
        return revision;
    }

    public async Task<T> RestoreVersionAsync(object id, int version, IClientSessionHandle? session = null, CancellationToken ct = default)
    {
        if (!typeof(IVersionable).IsAssignableFrom(typeof(T)))
            throw new NotSupportedException($"Entity type '{typeof(T).Name}' does not support versioning.");

        var revision = await GetRevisionByVersionAsync(id, version, session, ct);
        if (revision == null)
            throw new KeyNotFoundException($"Revision {version} for entity {id} not found.");

        var entity = BsonSerializer.Deserialize<T>(revision.Data);
        DecryptEntity(entity); // Decrypt to plaintext for application-side restore logic

        var objectId = ConvertToObjectId(id);
        var filter = Builders<T>.Filter.Eq(e => e.Id, objectId);

        var existingCursor = session != null
            ? await _collection.FindAsync(session, filter, new FindOptions<T> { Limit = 1 }, ct)
            : await _collection.FindAsync(filter, new FindOptions<T> { Limit = 1 }, ct);
        var existing = await existingCursor.FirstOrDefaultAsync(ct);

        var nextVersion = (existing?.Version ?? 0) + 1;
        entity.Version = nextVersion;
        entity.UpdatedAtUtc = DateTime.UtcNow;

        var encrypted = EncryptEntityForWrite(entity);

        if (session != null)
        {
            await _collection.ReplaceOneAsync(session, filter, encrypted, new ReplaceOptions { IsUpsert = true }, ct);
        }
        else
        {
            await _collection.ReplaceOneAsync(filter, encrypted, new ReplaceOptions { IsUpsert = true }, ct);
        }

        var operatorId = GetCurrentOperatorId();
        var historyCollectionName = CollectionName + "_History";
        var historyCollection = _collection.Database.GetCollection<EntityRevision>(historyCollectionName);
        var doc = encrypted.ToBsonDocument(); // stores encrypted state

        var restoreRevision = new EntityRevision
        {
            EntityId = objectId.ToString(),
            Version = nextVersion,
            Data = doc,
            ChangedBy = operatorId,
            Action = $"Restore (v{version})"
        };

        if (session != null)
            await historyCollection.InsertOneAsync(session, restoreRevision, null, ct);
        else
            await historyCollection.InsertOneAsync(restoreRevision, null, ct);

        return entity;
    }

    // ─── Sensitive Data Masking operations ────────────────────────────────

    public T MaskSensitiveFields(T entity)
    {
        if (entity == null) return null!;
        if (_userContext?.User != null && _userContext.User.HasClaim("scope", "view:pii"))
        {
            return entity;
        }
        return _encryptionService.MaskSensitiveFields(entity);
    }

    public async Task RestoreDeletedAsync(ObjectId id, IClientSessionHandle? session = null, CancellationToken ct = default)
    {
        if (!typeof(ISoftDelete).IsAssignableFrom(typeof(T)))
            throw new NotSupportedException($"Entity type '{typeof(T).Name}' does not support soft delete.");

        var filter = Builders<T>.Filter.Eq(e => e.Id, id);

        // Bypass soft delete filter to fetch the soft-deleted record
        var findOptions = new FindOptions<T> { Limit = 1 };
        var existingCursor = session != null
            ? await _collection.FindAsync(session, filter, findOptions, ct)
            : await _collection.FindAsync(filter, findOptions, ct);

        var entity = await existingCursor.FirstOrDefaultAsync(ct);
        if (entity == null)
            throw new KeyNotFoundException($"Entity with ID {id} not found in collection '{CollectionName}'");

        DecryptEntity(entity);

        var oldValues = new Dictionary<string, object?>();
        var properties = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        foreach (var prop in properties)
        {
            if (prop.CanRead) oldValues[prop.Name] = prop.GetValue(entity);
        }

        var oldIsDeleted = oldValues.TryGetValue("IsDeleted", out var oid) && oid is bool oidBool && oidBool;
        if (!oldIsDeleted) return; // Already active

        SetProperty(entity, "IsDeleted", false);
        SetProperty(entity, "DeletedAt", null as DateTime?);
        entity.UpdatedAtUtc = DateTime.UtcNow;
        var oldVersion = entity.Version;
        entity.Version = oldVersion + 1;

        var occFilter = Builders<T>.Filter.And(
            Builders<T>.Filter.Eq(e => e.Id, id),
            Builders<T>.Filter.Eq(e => e.Version, oldVersion)
        );

        var encrypted = EncryptEntityForWrite(entity);

        ReplaceOneResult replaceResult;
        if (session != null)
        {
            replaceResult = await _collection.ReplaceOneAsync(session, occFilter, encrypted, new ReplaceOptions { IsUpsert = false }, ct);
        }
        else
        {
            replaceResult = await _collection.ReplaceOneAsync(occFilter, encrypted, new ReplaceOptions { IsUpsert = false }, ct);
        }

        if (replaceResult.MatchedCount == 0)
        {
            var existsFilter = Builders<T>.Filter.Eq(e => e.Id, id);
            long existsCount = session != null
                ? await _collection.CountDocumentsAsync(session, existsFilter, cancellationToken: ct)
                : await _collection.CountDocumentsAsync(existsFilter, cancellationToken: ct);

            if (existsCount > 0)
            {
                throw new ConcurrencyException(id.ToString(), CollectionName,
                    $"Optimistic concurrency check failed during restoration. Document with ID '{id}' was modified by another operation.");
            }
            throw new KeyNotFoundException($"Entity with ID {id} not found or modified during restoration.");
        }

        if (entity is IVersionable)
        {
            var historyCollectionName = CollectionName + "_History";
            var historyCollection = _collection.Database.GetCollection<EntityRevision>(historyCollectionName);
            var revision = new EntityRevision
            {
                EntityId = id.ToString(),
                Version = entity.Version,
                Data = encrypted.ToBsonDocument(),
                ChangedBy = GetCurrentOperatorId(),
                Action = "RestoreFromSoftDelete"
            };
            if (session != null)
                await historyCollection.InsertOneAsync(session, revision, null, ct);
            else
                await historyCollection.InsertOneAsync(revision, null, ct);
        }

        if (_auditSink != null)
        {
            var entry = AuditLogEntry.ForRestore(
                GetCurrentOperatorId(),
                typeof(T).FullName ?? typeof(T).Name,
                id.ToString(),
                CollectionName);
            await _auditSink.WriteAsync(entry, ct);
        }
    }

    public async Task<IReadOnlyList<TResult>> AggregateAsync<TResult>(PipelineDefinition<T, TResult> pipeline, IClientSessionHandle? session = null, CancellationToken ct = default)
    {
        var collection = _collection.WithReadPreference(ReadPreference.SecondaryPreferred);
        var cursor = session != null
            ? await collection.AggregateAsync(session, pipeline, cancellationToken: ct)
            : await collection.AggregateAsync(pipeline, cancellationToken: ct);
        return await cursor.ToListAsync(ct);
    }

    // ─── Private Helpers ──────────────────────────────────────────────────

    private T EncryptEntityForWrite(T entity)
    {
        return _encryptionService.EncryptEntityForWrite(entity);
    }

    private void DecryptEntity(T? entity)
    {
        _encryptionService.DecryptEntity(entity);
    }

    private T CloneEntity(T entity)
    {
        return EntityEncryptionService<T>.CloneEntity(entity);
    }

    private static object? GetDiffValue(PropertyInfo prop, object? val)
    {
        return EntityEncryptionService<T>.GetDiffValue(prop, val);
    }

    private static ObjectId ConvertToObjectId(object id) => id switch
    {
        ObjectId oid => oid,
        string s when ObjectId.TryParse(s, out var oid) => oid,
        _ => throw new ArgumentException($"Cannot convert '{id}' of type '{id?.GetType().Name}' to ObjectId", nameof(id))
    };

    private string GetCurrentOperatorId() => _userContext?.OperatorId ?? "system";

    private FilterDefinition<T> ApplySoftDeleteFilter(FilterDefinition<T> filter)
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

        return filter;
    }

    private static Expression<Func<T, bool>> ApplySoftDeleteFilter(Expression<Func<T, bool>>? filter)
    {
        if (typeof(ISoftDelete).IsAssignableFrom(typeof(T)))
        {
            if (filter == null)
            {
                var parameter = Expression.Parameter(typeof(T), "x");
                var property = Expression.Property(parameter, nameof(ISoftDelete.IsDeleted));
                var notExpression = Expression.Not(property);
                return Expression.Lambda<Func<T, bool>>(notExpression, parameter);
            }
            else
            {
                var parameter = filter.Parameters[0];
                var property = Expression.Property(parameter, nameof(ISoftDelete.IsDeleted));
                var notExpression = Expression.Not(property);
                var combined = Expression.AndAlso(filter.Body, notExpression);
                return Expression.Lambda<Func<T, bool>>(combined, parameter);
            }
        }
        return filter ?? (x => true);
    }

    private static void SetProperty(object obj, string propertyName, object? value)
    {
        EntityEncryptionService<T>.SetProperty(obj, propertyName, value);
    }

    private static Expression<Func<T, bool>> BuildExpression(SearchCriterion[] criteria)
    {
        return DynamicExpressionBuilder.BuildExpression<T>(criteria);
    }

    private static SortDefinition<T>? BuildSortDefinition(PagedRequest request)
    {
        if (request.CursorInfo != null)
        {
            return request.CursorInfo.Order == SortOrder.Ascending
                ? Builders<T>.Sort.Ascending(request.CursorInfo.FieldName)
                : Builders<T>.Sort.Descending(request.CursorInfo.FieldName);
        }

        if (request.SortBy != null)
        {
            return request.SortBy.Order == SortOrder.Ascending
                ? Builders<T>.Sort.Ascending(request.SortBy.FieldName)
                : Builders<T>.Sort.Descending(request.SortBy.FieldName);
        }

        return Builders<T>.Sort.Descending(e => e.Id);
    }

    private static BsonDocument BuildBsonFilter(SearchCriterion[] criteria)
    {
        var filterDoc = new BsonDocument();
        foreach (var criterion in criteria)
        {
            var valueDoc = ConvertToBsonValue(criterion.Value);
            BsonValue operatorValue = criterion.Operator switch
            {
                SearchOperator.Equals => new BsonDocument("$eq", valueDoc),
                SearchOperator.NotEquals => new BsonDocument("$ne", valueDoc),
                SearchOperator.GreaterThan => new BsonDocument("$gt", valueDoc),
                SearchOperator.LessThan => new BsonDocument("$lt", valueDoc),
                SearchOperator.GreaterThanOrEqual => new BsonDocument("$gte", valueDoc),
                SearchOperator.LessThanOrEqual => new BsonDocument("$lte", valueDoc),
                SearchOperator.Contains => new BsonRegularExpression(EscapeRegex(criterion.Value?.ToString()), "i"),
                SearchOperator.StartsWith => new BsonRegularExpression("^" + EscapeRegex(criterion.Value?.ToString()), "i"),
                SearchOperator.EndsWith => new BsonRegularExpression(EscapeRegex(criterion.Value?.ToString()) + "$", "i"),
                SearchOperator.In => new BsonDocument("$in", new BsonArray(BuildBsonArray(criterion.Value))),
                _ => throw new NotSupportedException($"Operator '{criterion.Operator}' is not supported in Bson filters.")
            };

            if (operatorValue is BsonRegularExpression)
            {
                filterDoc[criterion.Field] = operatorValue;
            }
            else
            {
                if (filterDoc.Contains(criterion.Field) && filterDoc[criterion.Field].IsBsonDocument)
                {
                    filterDoc[criterion.Field].AsBsonDocument.Merge(operatorValue.AsBsonDocument);
                }
                else
                {
                    filterDoc[criterion.Field] = operatorValue;
                }
            }
        }
        return filterDoc;
    }

    private static BsonValue ConvertToBsonValue(object? value) => value switch
    {
        null => BsonNull.Value,
        ObjectId oid => oid,
        DateTime dt => dt,
        string s => s,
        int i => i,
        long l => l,
        double d => d,
        bool b => b,
        _ => value.ToString() ?? string.Empty
    };

    private static IEnumerable<BsonValue> BuildBsonArray(object? value)
    {
        if (value is System.Collections.IEnumerable enumerable)
        {
            foreach (var item in enumerable)
            {
                yield return ConvertToBsonValue(item);
            }
        }
        else if (value != null)
        {
            yield return ConvertToBsonValue(value);
        }
    }

    private static string EscapeRegex(string? pattern)
    {
        if (string.IsNullOrEmpty(pattern)) return string.Empty;
        return System.Text.RegularExpressions.Regex.Escape(pattern);
    }

    private static object? GetUnifiedPropertyValue(UnifiedSearchResult result, string fieldName)
    {
        if (fieldName.Equals("EntityId", StringComparison.OrdinalIgnoreCase)) return result.EntityId;
        if (fieldName.Equals("CollectionsName", StringComparison.OrdinalIgnoreCase)) return result.CollectionsName;
        if (fieldName.Equals("EntityType", StringComparison.OrdinalIgnoreCase)) return result.EntityType;

        return result.Properties.TryGetValue(fieldName, out var val) ? val : null;
    }

    private async Task AuditReadAsync(string entityId, CancellationToken ct)
    {
        if (_auditSink == null) return;
        var hasAttr = typeof(T).GetCustomAttribute<ReadAuditedAttribute>() != null;
        if (!hasAttr) return;

        var entry = AuditLogEntry.ForRead(
            GetCurrentOperatorId(),
            typeof(T).FullName ?? typeof(T).Name,
            entityId,
            CollectionName);
        await _auditSink.WriteAsync(entry, ct);
    }

    private async Task AuditReadsAsync(IEnumerable<string> entityIds, CancellationToken ct)
    {
        if (_auditSink == null) return;
        var hasAttr = typeof(T).GetCustomAttribute<ReadAuditedAttribute>() != null;
        if (!hasAttr) return;

        var entries = entityIds.Select(id => AuditLogEntry.ForRead(
            GetCurrentOperatorId(),
            typeof(T).FullName ?? typeof(T).Name,
            id,
            CollectionName)).ToList();

        if (entries.Any())
        {
            await _auditSink.WriteManyAsync(entries, ct);
        }
    }
}
