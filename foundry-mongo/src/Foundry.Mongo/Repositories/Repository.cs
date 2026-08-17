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
using Foundry.Mongo.Services;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace Foundry.Mongo.Repositories;

/// <summary>
/// Default repository implementation providing full CRUD operations with soft-delete via ISoftDelete, cursor/offset pagination, dynamic expression search, and audit trail.
/// Uses MongoDB.Driver v3 native async APIs exclusively — zero blocking calls or synchronous overloads.
/// </summary>
/// <remarks>
/// The rules this class used to spell out inline are now collaborators, and it orchestrates them:
/// <see cref="EntityAccessPolicy{T}"/> decides what a caller may see and change,
/// <see cref="EntitySearchTranslator{T}"/> turns a search request into a query,
/// <see cref="EntityWriteGuard{T}"/> says what has to be true for a write to land, and
/// <see cref="Foundry.Mongo.Services.EntityVersioningService{T}"/> owns the revision history.
/// What is left here is collection access, paging, and the order the steps happen in — which is the
/// part that genuinely differs per operation and is the reason a fused write method was never worth
/// building. Add a rule to a collaborator, not to this file: one rule copied across a 2,500-line
/// class is how three separate isolation defects got in.
/// </remarks>
public sealed class Repository<T> : IRepository<T> where T : class, IEntity<ObjectId>
{
    private readonly IMongoCollection<T> _collection;
    private readonly IAuditSink? _auditSink;
    private readonly ICurrentUserContext? _userContext;

    /// <summary>Ambient tenant, used to stamp audit entries. See <see cref="WriteAuditAsync"/>.</summary>
    private readonly Foundry.Core.Tenant.ITenantContext? _tenantContext;
    private readonly IEncryptionProvider? _encryptionProvider;

    /// <summary>
    /// What this caller may see and change. Every isolation decision this repository makes is asked
    /// of this object rather than computed here, so there is one place to read the rule and one place
    /// to test it — and it can be tested without a database, which the rule never could before.
    /// </summary>
    private readonly EntityAccessPolicy<T> _accessPolicy;

    /// <summary>
    /// How a caller's search request becomes a MongoDB query. Kept beside the access policy because
    /// it needs one: a pipeline is isolated as it is assembled, and criteria are entitled before they
    /// are compiled.
    /// </summary>
    private readonly EntitySearchTranslator<T> _searchTranslator;

    /// <summary>
    /// What has to be true for a write to land: the ambient tenant is stamped on, and the stored
    /// version is still the one that was read. Both rules were previously spelled out at every write
    /// site, which is how the read half's copies drifted.
    /// </summary>
    private readonly EntityWriteGuard<T> _writeGuard;

    private readonly EntityEncryptionService<T> _encryptionService;
    private readonly EntityAuditService<T> _auditService;

    /// <summary>
    /// Writes one audit entry, stamped with the ambient tenant.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every audit write in this class goes through here rather than touching the sink directly.
    /// There are fifteen places that construct an <see cref="AuditLogEntry"/> and none of them set
    /// the tenant, so stamping at each construction site would have been fifteen chances to miss
    /// one — and a future sixteenth site would have missed it by default. Stamping at the single
    /// point where entries leave the repository means a new construction site inherits it.
    /// </para>
    /// <para>
    /// The tenant was added to <see cref="AuditLogEntry"/> so the trail could be read back safely,
    /// and the stamping was put in <c>EntityAuditService</c> — which this class constructs and never
    /// calls. The field shipped, the index shipped, the read filtered on it, and nothing ever
    /// populated it: 3 rows out of 2,090 carried a tenant, and all three were written by tests that
    /// handed the sink an entry directly. Fixing the abstraction did nothing because the abstraction
    /// is not on the path.
    /// </para>
    /// </remarks>
    private Task WriteAuditAsync(AuditLogEntry entry, CancellationToken ct)
        => _auditSink is null
            ? Task.CompletedTask
            : _auditSink.WriteAsync(entry with { TenantId = _tenantContext?.TenantId }, ct);

    /// <inheritdoc cref="WriteAuditAsync"/>
    private Task WriteAuditManyAsync(IReadOnlyList<AuditLogEntry> entries, CancellationToken ct)
        => _auditSink is null
            ? Task.CompletedTask
            : _auditSink.WriteManyAsync(
                entries.Select(e => e with { TenantId = _tenantContext?.TenantId }).ToList(), ct);

    /// <summary>
    /// Where a revision snapshot goes and how it is read back. The shadow collection's name was
    /// derived at all twelve sites that touched history, so writer and reader agreed by coincidence
    /// rather than by construction; now one method derives it and both sides call that.
    /// </summary>
    private readonly EntityVersioningService<T> _versioningService;

    private readonly EntityIndexManager<T> _indexManager;

    public string CollectionName => _collection.CollectionNamespace.CollectionName;

    public int MaxDepthCap { get; set; } = 10_000;

    public IMongoCollection<T> Collection => _collection;

    /// <inheritdoc />
    public IQueryable<T> Query() => _collection.AsQueryable().Where(_accessPolicy.ApplyReadFilters(null));

    public Repository(
        IMongoDatabase db,
        IAuditSink? auditSink = null,
        ICurrentUserContext? userContext = null,
        IEncryptionProvider? encryptionProvider = null,
        string? collectionName = null,
        Foundry.Core.Tenant.ITenantContext? tenantContext = null,
        Foundry.Mongo.DependencyInjection.FoundryMongoOptions? mongoOptions = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        _auditSink = auditSink;
        _userContext = userContext;
        _tenantContext = tenantContext;
        _accessPolicy = new EntityAccessPolicy<T>(tenantContext, userContext, mongoOptions?.AllowUnauthenticatedFullReads ?? false);
        _searchTranslator = new EntitySearchTranslator<T>(_accessPolicy);

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
        // The tenant context reaches the audit service, not only the write guard below. Without it
        // every entry is written with a null TenantId, and the audit collection has no way to say
        // which tenant an entry belongs to -- which is exactly what stops the trail from ever being
        // safely readable by the people it is about.
        _auditService = new EntityAuditService<T>(auditSink, userContext, tenantContext);
        _versioningService = new EntityVersioningService<T>(db);

        var actualCollectionName = collectionName ?? typeof(T).Name.Pluralize();
        _collection = db.GetCollection<T>(actualCollectionName);
        _indexManager = new EntityIndexManager<T>(_collection);
        _writeGuard = new EntityWriteGuard<T>(_collection, _accessPolicy, tenantContext);
    }

    public async Task<T?> GetByIdAsync(object id, IClientSessionHandle? session = null, CancellationToken ct = default)
    {
        var objectId = ConvertToObjectId(id);
        var filter = Builders<T>.Filter.Eq(e => e.Id, objectId);
        filter = _accessPolicy.ApplyReadFilters(filter);
        
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

        return MaskForCaller(entity);
    }

    // ─── Create operations ────────────────────────────────────────────────

    public async Task InsertAsync(T entity, IClientSessionHandle? session = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entity);

        _writeGuard.StampTenant(entity);
        _accessPolicy.StampOwner(entity);

        var now = DateTime.UtcNow;
        entity.CreatedAtUtc = now;
        entity.UpdatedAtUtc = now;
        entity.Version = 1;

        // Assign the id here rather than letting the driver generate one during InsertOneAsync.
        //
        // The driver stamps the generated id on the instance it is handed, and when any property is
        // encrypted or masked that instance is a clone produced by EncryptEntityForWrite -- so the
        // id landed on the copy and the caller's entity kept ObjectId.Empty. Everything downstream
        // read the caller's copy: the POST response returned an id of all zeroes for a record that
        // had in fact been stored under a real one, and the revision snapshot and audit entry below
        // were both keyed to 000000000000000000000000, so the audit trail could not tell one insert
        // of an encrypted entity from another.
        //
        // Generating it before the clone is taken means both instances carry the same id and there
        // is nothing to copy back afterwards -- which matters because Id is init-only and cannot be
        // assigned after construction. Entities with no encrypted properties were never affected,
        // because EncryptEntityForWrite returns the same reference for them; that is why this went
        // unnoticed.
        if (entity.Id == ObjectId.Empty)
        {
            SetProperty(entity, nameof(entity.Id), ObjectId.GenerateNewId());
        }

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
            await _versioningService.SaveRevisionAsync(
                CollectionName, entity.Id.ToString(), entity.Version, encrypted.ToBsonDocument(),
                operatorId, "Insert", session, ct);
        }

        if (_auditSink != null)
        {
            var entry = AuditLogEntry.ForInsert(
                operatorId,
                typeof(T).FullName ?? typeof(T).Name,
                entity.Id.ToString(),
                CollectionName);
            await WriteAuditAsync(entry, ct);
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
            _writeGuard.StampTenant(entity);
            _accessPolicy.StampOwner(entity);
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
            var revisions = encryptedList.Select(entity => new EntityRevision
            {
                EntityId = entity.Id.ToString(),
                Version = entity.Version,
                Data = entity.ToBsonDocument(),
                ChangedBy = operatorId,
                Action = "Insert"
            }).ToList();

            await _versioningService.SaveRevisionsAsync(CollectionName, revisions, session, ct);
        }

        if (_auditSink != null)
        {
            var auditEntries = list.Select(entity => AuditLogEntry.ForInsert(
                operatorId,
                typeof(T).FullName ?? typeof(T).Name,
                entity.Id.ToString(),
                CollectionName)).ToList();

            await WriteAuditManyAsync(auditEntries, ct);
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
        var finalFilter = _accessPolicy.ApplyReadFilters(filter);
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

        return MaskForCaller(items);
    }

    public async Task<long> CountAsync(Expression<Func<T, bool>>? filter = null, IClientSessionHandle? session = null, CancellationToken ct = default)
    {
        var finalFilter = _accessPolicy.ApplyReadFilters(filter);
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
            mongoFilter = _accessPolicy.ApplyReadFilters(mongoFilter);
            mongoFilter = Builders<T>.Filter.And(mongoFilter, Builders<T>.Filter.Where(seekFilter));

            var sortDef = EntitySearchTranslator<T>.BuildSortDefinition(request);
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

            // The seek cursor is taken from the unmasked row, before masking. A cursor built from a
            // masked value would be a position no document holds, so the next page would come back
            // empty or wrong -- and only for callers without the scope, on entities that happen to
            // page by a masked field, which is about as hard to reproduce as a defect gets.
            CursorSeekInfo? nextCursor = null;
            if (hasNextPage && pageItems.Count > 0)
            {
                var lastItem = pageItems[^1];
                nextCursor = CursorSeekInfo.FromValue(lastItem, request.CursorInfo.FieldName, request.CursorInfo.Order);
            }

            var maskedPage = MaskForCaller(pageItems);

            if (nextCursor != null)
            {
                return PagedResult<T>.WithCursor(maskedPage, maskedPage.Count + 1, request.PageNumber, request.PageSize, nextCursor);
            }
            else
            {
                return new PagedResult<T>
                {
                    Items = maskedPage,
                    TotalRecords = maskedPage.Count,
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
            mongoFilter = _accessPolicy.ApplyReadFilters(mongoFilter);

            var totalRecords = session != null
                ? await _collection.CountDocumentsAsync(session, mongoFilter, cancellationToken: ct)
                : await _collection.CountDocumentsAsync(mongoFilter, cancellationToken: ct);

            var (skip, take) = OffsetPaginationHelper.GetSkipTakeValues(request);
            var sortDef = EntitySearchTranslator<T>.BuildSortDefinition(request);
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

            return PagedResult<T>.From(MaskForCaller(items), totalRecords, request.PageNumber, request.PageSize);
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
        mongoFilter = _accessPolicy.ApplyReadFilters(mongoFilter);

        var sortDef = EntitySearchTranslator<T>.BuildSortDefinition(request);

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
            return MaskForCaller(items);
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
            return MaskForCaller(items);
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
        var filter = _accessPolicy.ScopeToOwner(_accessPolicy.ScopeToTenant(Builders<T>.Filter.Eq(e => e.Id, objectId)));

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
        var oldVersion = EntityWriteGuard<T>.StoredVersion(oldValues);
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
        var occFilter = _writeGuard.OccFilter(objectId, oldVersion);

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

        await _writeGuard.ThrowOnConcurrencyConflictAsync(
            replaceResult, objectId, id, WriteOperation.Update, session, ct);

        // Historical Revision snapshot (stores encrypted state)
        if (entityAfter is IVersionable)
        {
            await _versioningService.SaveRevisionAsync(
                CollectionName, entityAfter.Id.ToString(), entityAfter.Version, encrypted.ToBsonDocument(),
                operatorId, isSoftDeletedNow ? "SoftDelete" : "Update", session, ct);
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

            await WriteAuditAsync(entry, ct);
        }
    }

    public async Task UpdateAsync(T entity, IClientSessionHandle? session = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        var operatorId = GetCurrentOperatorId();

        // The update is a whole-document replace, so an unstamped tenant in the request body would
        // be written verbatim -- letting a PUT move a row into another tenant.
        _writeGuard.StampTenant(entity);
        _accessPolicy.StampOwner(entity);

        var oldVersion = entity.Version;
        var filter = _accessPolicy.ScopeToOwner(_accessPolicy.ScopeToTenant(Builders<T>.Filter.Eq(e => e.Id, entity.Id)));

        var existingCursor = session != null
            ? await _collection.FindAsync(session, filter, new FindOptions<T> { Limit = 1 }, ct)
            : await _collection.FindAsync(filter, new FindOptions<T> { Limit = 1 }, ct);

        var existing = await existingCursor.FirstOrDefaultAsync(ct);
        if (existing == null)
            throw new KeyNotFoundException($"Entity with ID {entity.Id} not found in collection '{CollectionName}'");

        DecryptEntity(existing);
        PreserveMaskedFieldsCallerCannotRead(entity, existing);

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
        var occFilter = _writeGuard.OccFilter(entity.Id, oldVersion);

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

        await _writeGuard.ThrowOnConcurrencyConflictAsync(
            replaceResult, entity.Id, entity.Id, WriteOperation.Update, session, ct);

        // Historical Revision snapshot (stores encrypted state)
        if (entity is IVersionable)
        {
            await _versioningService.SaveRevisionAsync(
                CollectionName, entity.Id.ToString(), entity.Version, encrypted.ToBsonDocument(),
                operatorId, isSoftDeletedNow ? "SoftDelete" : "Update", session, ct);
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

            await WriteAuditAsync(entry, ct);
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

        // Write filters, not read filters. This is a write that names its rows with a predicate, so
        // the candidates it loads have to be the ones the caller may change rather than the ones they
        // may see -- otherwise a read-exempt auditor or a SharedWith grantee replaces a row they were
        // only ever granted sight of, and nothing downstream re-checks. Selecting the rows is the
        // right place: the version check further down cannot refuse a write without reporting it as a
        // concurrency conflict.
        var finalFilter = _accessPolicy.ApplyWriteFilters(filter);

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
            var oldVersion = EntityWriteGuard<T>.StoredVersion(oldValues);
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
            var replaceFilter = _writeGuard.OccFilter(entityAfter.Id, oldVersion);
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
            _writeGuard.ThrowOnBulkConcurrencyConflict(bulkResult, writeModels.Count);

            var updateResult = new UpdateResult.Acknowledged(bulkResult.MatchedCount, bulkResult.ModifiedCount, null);
            results.Add(updateResult);

            await _versioningService.SaveRevisionsAsync(CollectionName, revisions, session, ct);

            if (_auditSink != null && auditEntries.Count > 0)
            {
                await WriteAuditManyAsync(auditEntries, ct);
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
            _writeGuard.StampTenant(entity);
            _accessPolicy.StampOwner(entity);
            var filter = _accessPolicy.ScopeToOwner(_accessPolicy.ScopeToTenant(Builders<T>.Filter.Eq(e => e.Id, entity.Id)));
            var findOptions = new FindOptions<T> { Limit = 1 };
            var existingCursor = session != null
                ? await _collection.FindAsync(session, filter, findOptions, ct)
                : await _collection.FindAsync(filter, findOptions, ct);

            var existing = await existingCursor.FirstOrDefaultAsync(ct);
            if (existing == null) continue;

            DecryptEntity(existing);
            PreserveMaskedFieldsCallerCannotRead(entity, existing);

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

            var oldVersion = EntityWriteGuard<T>.StoredVersion(oldValues);
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

            var replaceFilter = _writeGuard.OccFilter(entity.Id, oldVersion);
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

            _writeGuard.ThrowOnBulkConcurrencyConflict(bulkResult, writeModels.Count);

            await _versioningService.SaveRevisionsAsync(CollectionName, revisions, session, ct);

            if (_auditSink != null && auditEntries.Count > 0)
            {
                await WriteAuditManyAsync(auditEntries, ct);
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
        var filter = _accessPolicy.ScopeToOwner(_accessPolicy.ScopeToTenant(Builders<T>.Filter.Eq(e => e.Id, objectId)));

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
                await _versioningService.SaveRevisionAsync(
                    CollectionName, updatedEntity.Id.ToString(), updatedEntity.Version,
                    updatedEntity.ToBsonDocument(), // stores encrypted database state
                    operatorId, "SoftDelete", session, ct);
            }

            if (_auditSink != null)
            {
                var entry = AuditLogEntry.ForSoftDelete(
                    operatorId,
                    typeof(T).FullName ?? typeof(T).Name,
                    entity.Id.ToString(),
                    CollectionName);
                await WriteAuditAsync(entry, ct);
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
                // For hard deletes, we preserve the last database-side (encrypted) BSON representation in history
                var encrypted = EncryptEntityForWrite(entity);
                await _versioningService.SaveRevisionAsync(
                    CollectionName, entity.Id.ToString(), entity.Version + 1, encrypted.ToBsonDocument(),
                    operatorId, "HardDelete", session, ct);
            }

            if (_auditSink != null)
            {
                var entry = AuditLogEntry.ForHardDelete(
                    operatorId,
                    typeof(T).FullName ?? typeof(T).Name,
                    entity.Id.ToString(),
                    CollectionName);
                await WriteAuditAsync(entry, ct);
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
        var expression = _searchTranslator.BuildExpression(criteria);
        var finalFilter = _accessPolicy.ApplyReadFilters(expression);

        var cursor = session != null
            ? await _collection.FindAsync(session, finalFilter, null, ct)
            : await _collection.FindAsync(finalFilter, null, ct);

        var items = await cursor.ToListAsync(ct);
        foreach (var item in items) DecryptEntity(item);
        
        await AuditReadsAsync(items.Select(e => e.Id.ToString()), ct);

        return MaskForCaller(items);
    }

    public async Task<PagedResult<T>> SearchPagedAsync(
        SearchCriterion[] criteria,
        PagedRequest pageRequest,
        IClientSessionHandle? session = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        ArgumentNullException.ThrowIfNull(pageRequest);

        var expression = _searchTranslator.BuildExpression(criteria);
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

        var pageNumber = request.Pagination?.PageNumber ?? 1;
        var pageSize = request.Pagination?.PageSize ?? 20;

        var mainPipeline = _searchTranslator.BuildCrossCollectionPipeline(request, list, pageNumber, pageSize);

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

    /// <summary>
    /// Whether the caller may see this record at all, for the purpose of reading things attached to it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Revision history lives in a second collection keyed only by entity id, and was read with that
    /// id and nothing else. An id is not a secret — the access policy's own <c>ScopeToTenant</c>
    /// says so, and applies the tenant filter to writes for exactly that reason — so a caller could
    /// name another tenant's record and read its full history. <c>[Encrypt]</c> fields stay ciphertext
    /// in a revision, but <c>[Mask]</c> and <c>[SensitiveData]</c> ones do not: masking happens on the
    /// way out, so those sit in the revision in clear text.
    /// </para>
    /// <para>
    /// Tenant and owner, deliberately without the soft-delete filter. Reading the history of a deleted
    /// record is a reasonable thing to want and is what <c>RestoreVersionAsync</c> needs; the question
    /// here is whose record it is, not whether it is live. This is the same filter the restore paths
    /// already use, and the same rule the workflow history endpoint applies by loading the entity
    /// through the repository before serving anything attached to it.
    /// </para>
    /// </remarks>
    private async Task<bool> CallerMaySeeRecordAsync(ObjectId id, IClientSessionHandle? session, CancellationToken ct)
    {
        var filter = _accessPolicy.ScopeToOwner(_accessPolicy.ScopeToTenant(Builders<T>.Filter.Eq(e => e.Id, id)));
        var options = new FindOptions<T> { Limit = 1, Projection = Builders<T>.Projection.Include(e => e.Id) };

        var cursor = session != null
            ? await _collection.FindAsync(session, filter, options, ct)
            : await _collection.FindAsync(filter, options, ct);

        return await cursor.AnyAsync(ct);
    }

    public async Task<IReadOnlyList<EntityRevision>> GetRevisionsAsync(object id, IClientSessionHandle? session = null, CancellationToken ct = default)
    {
        var objectId = ConvertToObjectId(id);

        // Empty rather than an error: the same answer a caller gets for an id that does not exist,
        // so this does not confirm that the record exists in some other tenant.
        if (!await CallerMaySeeRecordAsync(objectId, session, ct)) return Array.Empty<EntityRevision>();

        var objectIdStr = objectId.ToString();

        var revisions = await _versioningService.GetRevisionsAsync(CollectionName, objectIdStr, session, ct);
        if (revisions.Any())
        {
            await AuditReadAsync(objectIdStr, ct);
        }
        return revisions;
    }

    public async Task<EntityRevision?> GetRevisionByVersionAsync(object id, int version, IClientSessionHandle? session = null, CancellationToken ct = default)
    {
        var objectId = ConvertToObjectId(id);

        if (!await CallerMaySeeRecordAsync(objectId, session, ct)) return null;

        return await ReadRevisionAsync(objectId, version, session, ct);
    }

    /// <summary>
    /// Reads one revision without checking visibility. Callers must have established it themselves.
    /// </summary>
    /// <remarks>
    /// Private, and named so that is unmissable. <c>RestoreVersionAsync</c> loads the live row through
    /// the tenant- and owner-scoped filter as its next step, so routing it through the public gated
    /// reader would query the same row twice to answer the same question.
    /// </remarks>
    private async Task<EntityRevision?> ReadRevisionAsync(
        ObjectId objectId, int version, IClientSessionHandle? session, CancellationToken ct)
    {
        var objectIdStr = objectId.ToString();

        var revision = await _versioningService.GetRevisionByVersionAsync(
            CollectionName, objectIdStr, version, session, ct);
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

        // Visibility is established below, against the live row, with ScopeToOwner(ScopeToTenant(...)).
        // Reading the revision through the gated public method would ask the same question twice.
        var revision = await ReadRevisionAsync(ConvertToObjectId(id), version, session, ct);
        if (revision == null)
            throw new KeyNotFoundException($"Revision {version} for entity {id} not found.");

        var entity = BsonSerializer.Deserialize<T>(revision.Data);
        DecryptEntity(entity); // Decrypt to plaintext for application-side restore logic

        var objectId = ConvertToObjectId(id);
        var filter = _accessPolicy.ScopeToOwner(_accessPolicy.ScopeToTenant(Builders<T>.Filter.Eq(e => e.Id, objectId)));

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
        var doc = encrypted.ToBsonDocument(); // stores encrypted state

        await _versioningService.SaveRevisionAsync(
            CollectionName, objectId.ToString(), nextVersion, doc,
            operatorId, $"Restore (v{version})", session, ct);

        return entity;
    }

    // ─── Sensitive Data Masking operations ────────────────────────────────

    public T MaskSensitiveFields(T entity)
    {
        if (entity == null) return null!;
        if (!HasMaskedProperties) return entity;

        return _encryptionService.MaskSensitiveFields(entity, _accessPolicy.ShouldMask);
    }

    /// <summary>Whether this caller may see every masked property on this entity in full.</summary>
    private bool MayViewEverySensitiveCategory
        => MaskedCategories.All(category =>
            _userContext?.User?.HasClaim(
                ViewSensitiveDataScope.ClaimType, ViewSensitiveDataScope.For(category)) == true);

    /// <summary>
    /// The distinct categories this entity declares, read once per closed generic type.
    /// </summary>
    private static readonly string[] MaskedCategories = typeof(T)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Select(p => p.GetCustomAttribute<Foundry.Core.Entities.SensitiveDataAttribute>())
        .Where(a => a is { Protection: Foundry.Core.Entities.ProtectionType.Mask })
        .Select(a => a!.Category)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    /// <summary>
    /// Masks every property an entity declares as <c>Mask</c>-protected, unless the caller may see them.
    /// </summary>
    /// <remarks>
    /// Applied at the repository rather than in each endpoint so that every transport is covered by
    /// one rule. REST, GraphQL, the generated SDKs and anything else reading through
    /// <c>IRepository&lt;T&gt;</c> get the same answer; masking per transport is how this codebase has
    /// repeatedly ended up with two implementations of one rule, and the route prefix went wrong in
    /// six places that way.
    /// </remarks>
    private T? MaskForCaller(T? entity)
    {
        if (entity is null || !HasMaskedProperties || MayViewEverySensitiveCategory) return entity;

        return MaskSensitiveFields(entity);
    }

    private IReadOnlyList<T> MaskForCaller(IReadOnlyList<T> entities)
    {
        if (entities.Count == 0 || !HasMaskedProperties || MayViewEverySensitiveCategory) return entities;

        var masked = new List<T>(entities.Count);
        foreach (var entity in entities) masked.Add(MaskSensitiveFields(entity));

        return masked;
    }

    /// <summary>
    /// Whether this entity type declares anything to mask, read once per closed generic type.
    /// </summary>
    private static readonly bool HasMaskedProperties = typeof(T)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Select(p => p.GetCustomAttribute<Foundry.Core.Entities.SensitiveDataAttribute>())
        .Any(a => a is { Protection: Foundry.Core.Entities.ProtectionType.Mask });

    /// <summary>
    /// Preserves masked fields that the caller cannot read, preventing data loss through updates.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The old guard caught only the mask being echoed back, so a well-behaved client that omitted
    /// the field instead wiped it. Masking is applied on serialization while the update is a
    /// whole-document replace, so the server must reconcile: a caller who was not allowed to read
    /// a field must not be able to change it.
    /// </para>
    /// <para>
    /// For each masked string property:
    /// - If the caller cannot view this category (ShouldMask returns true), the incoming value is
    ///   overwritten with the stored value. This preserves the field regardless of what was supplied
    ///   (mask echoed back, null, empty string, or attempted update).
    /// - If the caller can view this category (ShouldMask returns false), the update is allowed and
    ///   the existing guard against writing back the mask is kept in place as a client bug indicator.
    /// </para>
    /// <para>
    /// Non-string masked properties are skipped because string operations are not reliable on other
    /// property types in this context.
    /// </para>
    /// </remarks>
    private void PreserveMaskedFieldsCallerCannotRead(T incoming, T existing)
    {
        if (!HasMaskedProperties) return;

        foreach (var (property, attribute) in EntityEncryptionService<T>.GetSensitiveProperties())
        {
            if (attribute.Protection != Foundry.Core.Entities.ProtectionType.Mask) continue;
            if (property.PropertyType != typeof(string)) continue; // Skip non-string properties

            var stored = property.GetValue(existing) as string;
            var supplied = property.GetValue(incoming) as string;

            // If the caller cannot view this category, preserve the stored value regardless of what was supplied
            if (_accessPolicy.ShouldMask(attribute))
            {
                // Restore the stored value to prevent data loss
                property.SetValue(incoming, stored);
            }
            else
            {
                // Privileged caller: allow update but still guard against mask echo
                if (string.IsNullOrEmpty(stored) || supplied is null) continue;
                if (string.Equals(stored, supplied, StringComparison.Ordinal)) continue;

                if (!string.Equals(attribute.MaskValue(stored), supplied, StringComparison.Ordinal)) continue;

                throw new InvalidOperationException(
                    $"{typeof(T).Name}.{property.Name} was written back in its masked form, which would "
                    + "replace the stored value with the mask. Re-read the entity as a caller holding the "
                    + $"'{ViewSensitiveDataScope.ClaimValue}' scope, or build the update from a value the "
                    + "caller supplied rather than from a masked read.");
            }
        }
    }

    public async Task RestoreDeletedAsync(ObjectId id, IClientSessionHandle? session = null, CancellationToken ct = default)
    {
        if (!typeof(ISoftDelete).IsAssignableFrom(typeof(T)))
            throw new NotSupportedException($"Entity type '{typeof(T).Name}' does not support soft delete.");

        var filter = _accessPolicy.ScopeToOwner(_accessPolicy.ScopeToTenant(Builders<T>.Filter.Eq(e => e.Id, id)));

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

        var occFilter = _writeGuard.OccFilter(id, oldVersion);

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

        await _writeGuard.ThrowOnConcurrencyConflictAsync(
            replaceResult, id, id, WriteOperation.Restoration, session, ct);

        if (entity is IVersionable)
        {
            await _versioningService.SaveRevisionAsync(
                CollectionName, id.ToString(), entity.Version, encrypted.ToBsonDocument(),
                GetCurrentOperatorId(), "RestoreFromSoftDelete", session, ct);
        }

        if (_auditSink != null)
        {
            var entry = AuditLogEntry.ForRestore(
                GetCurrentOperatorId(),
                typeof(T).FullName ?? typeof(T).Name,
                id.ToString(),
                CollectionName);
            await WriteAuditAsync(entry, ct);
        }
    }

    public async Task<IReadOnlyList<TResult>> AggregateAsync<TResult>(PipelineDefinition<T, TResult> pipeline, IClientSessionHandle? session = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pipeline);

        // The caller's pipeline runs against the rows they are allowed to see, not the collection.
        //
        // It used to run against the collection: no tenant, no owner, no soft-delete predicate. That
        // made this the one method on IRepository<T> that does not enforce isolation, sitting beside
        // eleven that do, with nothing in its name or its documentation to say so. An escape hatch is
        // a defensible thing to offer; one that looks identical to the safe methods is not.
        //
        // Prepending rather than appending: a $match after a $group would filter on fields the
        // grouping has already discarded, and would have read every tenant's rows to build the groups
        // in the first place.
        var scoped = new PrependedStagePipelineDefinition<T, T, TResult>(
            PipelineStageDefinitionBuilder.Match(_accessPolicy.ApplyReadFilters(Builders<T>.Filter.Empty)),
            pipeline);

        var collection = _collection.WithReadPreference(ReadPreference.SecondaryPreferred);
        var cursor = session != null
            ? await collection.AggregateAsync(session, scoped, cancellationToken: ct)
            : await collection.AggregateAsync(scoped, cancellationToken: ct);
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

    private static void SetProperty(object obj, string propertyName, object? value)
    {
        EntityEncryptionService<T>.SetProperty(obj, propertyName, value);
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
        await WriteAuditAsync(entry, ct);
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
            await WriteAuditManyAsync(entries, ct);
        }
    }
}
