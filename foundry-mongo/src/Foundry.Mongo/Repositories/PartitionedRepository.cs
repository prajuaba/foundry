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

    public IMongoCollection<T> Collection => _activeRepository.Collection;
    public string CollectionName => _activeRepository.CollectionName;
    public int MaxDepthCap { get => _activeRepository.MaxDepthCap; set => _activeRepository.MaxDepthCap = value; }

    public PartitionedRepository(
        IMongoDatabase db,
        IAuditSink? auditSink = null,
        ICurrentUserContext? userContext = null,
        IEncryptionProvider? encryptionProvider = null,
        Foundry.Core.Tenant.ITenantContext? tenantContext = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _auditSink = auditSink;
        _userContext = userContext;
        _encryptionProvider = encryptionProvider;

        var partitionedAttribute = typeof(T).GetCustomAttribute<PartitionedAttribute>();
        _thresholdYears = partitionedAttribute?.ArchiveThresholdYears ?? 2;

        var baseCollectionName = typeof(T).Name.Pluralize();
        _activeRepository = new Repository<T>(db, auditSink, userContext, encryptionProvider, baseCollectionName, tenantContext);
        _deletedRepository = new Repository<T>(db, auditSink, userContext, encryptionProvider, $"{baseCollectionName}_Deleted", tenantContext);
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
            return new Repository<T>(_db, _auditSink, _userContext, _encryptionProvider, archiveCollectionName);
        });
    }

    private Repository<T> GetRepositoryForId(ObjectId id)
    {
        int year = id.CreationTime.Year;
        return IsInArchive(year) ? GetArchiveRepository(year) : _activeRepository;
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
        int year = objectId.CreationTime.Year;

        if (IsInArchive(year))
        {
            return await GetArchiveRepository(year).GetByIdAsync(objectId, session, ct);
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
        var repos = RouteRepositoriesByDateFilter(filter);
        if (repos.Count == 1)
        {
            return await repos[0].FindManyAsync(filter, sortBy, sortOrder, limit, session, ct);
        }

        var results = new List<T>();
        foreach (var repo in repos)
        {
            var items = await repo.FindManyAsync(filter, sortBy, sortOrder, limit, session, ct);
            results.AddRange(items);
        }
        return results.Take(limit).ToList();
    }

    public async Task<long> CountAsync(Expression<Func<T, bool>>? filter = null, IClientSessionHandle? session = null, CancellationToken ct = default)
    {
        var repos = RouteRepositoriesByDateFilter(filter);
        long total = 0;
        foreach (var repo in repos)
        {
            total += await repo.CountAsync(filter, session, ct);
        }
        return total;
    }

    public async Task<PagedResult<T>> GetPagedAsync(PagedRequest request, Expression<Func<T, bool>>? filter = null, IClientSessionHandle? session = null, CancellationToken ct = default)
    {
        var repos = RouteRepositoriesByDateFilter(filter);
        if (repos.Count == 1)
        {
            return await repos[0].GetPagedAsync(request, filter, session, ct);
        }

        var items = new List<T>();
        long total = 0;
        foreach (var repo in repos)
        {
            var res = await repo.GetPagedAsync(request, filter, session, ct);
            items.AddRange(res.Items);
            total += res.TotalRecords;
        }
        return new PagedResult<T> { Items = items.Take(request.PageSize).ToList(), TotalRecords = total, PageNumber = request.PageNumber, PageSize = request.PageSize };
    }

    public async Task<IReadOnlyList<T>> GetPagedItemsAsync(PagedRequest request, Expression<Func<T, bool>>? filter = null, IClientSessionHandle? session = null, CancellationToken ct = default)
    {
        var repos = RouteRepositoriesByDateFilter(filter);
        if (repos.Count == 1)
        {
            return await repos[0].GetPagedItemsAsync(request, filter, session, ct);
        }

        var items = new List<T>();
        foreach (var repo in repos)
        {
            items.AddRange(await repo.GetPagedItemsAsync(request, filter, session, ct));
        }
        return items.Take(request.PageSize).ToList();
    }

    public async Task UpdateByObjectIdAsync(object id, Func<T, T> updateSelector, string operatorId, IClientSessionHandle? session = null, CancellationToken ct = default)
    {
        var objectId = ConvertId(id);
        var repo = GetRepositoryForId(objectId);
        await repo.UpdateByObjectIdAsync(objectId, updateSelector, operatorId, session, ct);
    }

    public async Task UpdateAsync(T entity, IClientSessionHandle? session = null, CancellationToken ct = default)
    {
        var objectId = ConvertId(entity.Id);
        var repo = GetRepositoryForId(objectId);
        await repo.UpdateAsync(entity, session, ct);
    }

    public async Task<IReadOnlyList<UpdateResult>> BulkUpdateManyAsync(Expression<Func<T, bool>> filter, Func<T, T> updateSelector, IClientSessionHandle? session = null, CancellationToken ct = default)
    {
        var repos = RouteRepositoriesByDateFilter(filter);
        var results = new List<UpdateResult>();
        foreach (var repo in repos)
        {
            var res = await repo.BulkUpdateManyAsync(filter, updateSelector, session, ct);
            results.AddRange(res);
        }
        return results;
    }

    public async Task BulkUpdateAsync(IEnumerable<T> entities, IClientSessionHandle? session = null, CancellationToken ct = default)
    {
        var groups = entities.GroupBy(e => ConvertId(e.Id).CreationTime.Year);
        foreach (var group in groups)
        {
            var repo = IsInArchive(group.Key) ? GetArchiveRepository(group.Key) : _activeRepository;
            await repo.BulkUpdateAsync(group, session, ct);
        }
    }

    public async Task DeleteByObjectIdAsync(object id, string operatorId, IClientSessionHandle? session = null, CancellationToken ct = default)
    {
        var objectId = ConvertId(id);
        int year = objectId.CreationTime.Year;
        var sourceRepo = IsInArchive(year) ? GetArchiveRepository(year) : _activeRepository;

        var entity = await sourceRepo.GetByIdAsync(objectId, session, ct);
        if (entity == null) return;

        if (entity is ISoftDelete)
        {
            SetProperty(entity, "IsDeleted", true);
            SetProperty(entity, "DeletedAt", DateTime.UtcNow);

            var actualSession = session ?? await _db.Client.StartSessionAsync(cancellationToken: ct);
            var isLocalSession = session == null;

            try
            {
                if (isLocalSession) actualSession.StartTransaction();

                // Save to deleted repository
                await _deletedRepository.InsertAsync(entity, actualSession, ct);
                
                // Hard delete from source collection
                var filter = Builders<T>.Filter.Eq(e => e.Id, objectId);
                await sourceRepo.Collection.DeleteOneAsync(actualSession, filter, cancellationToken: ct);

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
        // For search criteria, fallback to active + deleted + all archives
        var activeResults = await _activeRepository.FindByCriteriaAsync(criteria, session, ct);
        var results = activeResults.ToList();

        foreach (var repo in _archiveRepositories.Values)
        {
            var res = await repo.FindByCriteriaAsync(criteria, session, ct);
            results.AddRange(res);
        }

        return results;
    }

    public async Task<PagedResult<UnifiedSearchResult>> CrossCollectionSearchAsync(CrossCollectionSearchRequest request, IClientSessionHandle? session = null, CancellationToken ct = default)
    {
        // Parallel cross collection search on active + archives
        var activeResults = await _activeRepository.CrossCollectionSearchAsync(request, session, ct);
        var results = activeResults.Items.ToList();
        long total = activeResults.TotalRecords;

        foreach (var repo in _archiveRepositories.Values)
        {
            var res = await repo.CrossCollectionSearchAsync(request, session, ct);
            results.AddRange(res.Items);
            total += res.TotalRecords;
        }

        return new PagedResult<UnifiedSearchResult> { Items = results, TotalRecords = total, PageNumber = activeResults.PageNumber, PageSize = activeResults.PageSize };
    }

    public async Task<PagedResult<T>> SearchPagedAsync(SearchCriterion[] criteria, PagedRequest pageRequest, IClientSessionHandle? session = null, CancellationToken ct = default)
    {
        var activePage = await _activeRepository.SearchPagedAsync(criteria, pageRequest, session, ct);
        var results = activePage.Items.ToList();
        long total = activePage.TotalRecords;

        foreach (var repo in _archiveRepositories.Values)
        {
            var res = await repo.SearchPagedAsync(criteria, pageRequest, session, ct);
            results.AddRange(res.Items);
            total += res.TotalRecords;
        }

        return new PagedResult<T> { Items = results.Take(pageRequest.PageSize).ToList(), TotalRecords = total, PageNumber = pageRequest.PageNumber, PageSize = pageRequest.PageSize };
    }

    public async Task CreateIndexesAsync(CancellationToken ct = default)
    {
        await _activeRepository.CreateIndexesAsync(ct);
        await _deletedRepository.CreateIndexesAsync(ct);
        foreach (var repo in _archiveRepositories.Values)
        {
            await repo.CreateIndexesAsync(ct);
        }
    }

    public async Task<IReadOnlyList<EntityRevision>> GetRevisionsAsync(object id, IClientSessionHandle? session = null, CancellationToken ct = default)
    {
        var objectId = ConvertId(id);
        int year = objectId.CreationTime.Year;
        var repo = IsInArchive(year) ? GetArchiveRepository(year) : _activeRepository;
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
        int year = objectId.CreationTime.Year;
        var repo = IsInArchive(year) ? GetArchiveRepository(year) : _activeRepository;
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
        int year = objectId.CreationTime.Year;
        var repo = IsInArchive(year) ? GetArchiveRepository(year) : _activeRepository;
        return await repo.RestoreVersionAsync(objectId, version, session, ct);
    }

    public T MaskSensitiveFields(T entity)
    {
        return _activeRepository.MaskSensitiveFields(entity);
    }

    public async Task RestoreDeletedAsync(ObjectId id, IClientSessionHandle? session = null, CancellationToken ct = default)
    {
        var filter = Builders<T>.Filter.Eq(e => e.Id, id);
        var entity = await _deletedRepository.Collection.Find(session, filter).FirstOrDefaultAsync(ct);
        if (entity == null) return;

        SetProperty(entity, "IsDeleted", false);
        SetProperty(entity, "DeletedAt", null as DateTime?);

        int year = id.CreationTime.Year;
        var targetRepo = IsInArchive(year) ? GetArchiveRepository(year) : _activeRepository;

        var actualSession = session ?? await _db.Client.StartSessionAsync(cancellationToken: ct);
        var isLocalSession = session == null;

        try
        {
            if (isLocalSession) actualSession.StartTransaction();

            // Insert into active/archive
            await targetRepo.InsertAsync(entity, actualSession, ct);
            
            // Remove from deleted collection
            await _deletedRepository.Collection.DeleteOneAsync(actualSession, filter, cancellationToken: ct);

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
    }

    private List<string> GetArchiveCollectionNames()
    {
        var baseCollectionName = typeof(T).Name.Pluralize();
        var filter = new BsonDocument("name", new BsonRegularExpression($"^{baseCollectionName}_\\d{{4}}$"));
        var collections = _db.ListCollectionNames(new ListCollectionNamesOptions { Filter = filter }).ToList();
        return collections;
    }

    public async Task<IReadOnlyList<TResult>> AggregateAsync<TResult>(PipelineDefinition<T, TResult> pipeline, IClientSessionHandle? session = null, CancellationToken ct = default)
    {
        var serializer = BsonSerializer.LookupSerializer<T>();
        var rendered = pipeline.Render(new RenderArgs<T>(serializer, BsonSerializer.SerializerRegistry));
        var stagesList = rendered.Documents.ToList();

        var newStages = new List<BsonDocument>();
        var archives = GetArchiveCollectionNames();
        foreach (var archive in archives)
        {
            newStages.Add(new BsonDocument("$unionWith", new BsonDocument("coll", archive)));
        }
        newStages.AddRange(stagesList);

        var finalPipeline = PipelineDefinition<T, TResult>.Create(newStages);
        var collection = Collection.WithReadPreference(ReadPreference.SecondaryPreferred);

        var cursor = session != null
            ? await collection.AggregateAsync(session, finalPipeline, cancellationToken: ct)
            : await collection.AggregateAsync(finalPipeline, cancellationToken: ct);

        return await cursor.ToListAsync(ct);
    }

    private List<Repository<T>> RouteRepositoriesByDateFilter(Expression<Func<T, bool>>? filter)
    {
        var result = new List<Repository<T>>();
        if (filter == null)
        {
            result.Add(_activeRepository);
            return result;
        }

        var visitor = new DateRangeVisitor();
        visitor.Visit(filter);

        if (visitor.StartDate.HasValue || visitor.EndDate.HasValue)
        {
            var currentYear = DateTime.UtcNow.Year;
            var activeStartYear = currentYear - _thresholdYears + 1;
            
            var startYear = visitor.StartDate?.Year ?? (currentYear - 10);
            var endYear = visitor.EndDate?.Year ?? currentYear;

            for (int y = startYear; y <= endYear; y++)
            {
                if (y < activeStartYear)
                {
                    result.Add(GetArchiveRepository(y));
                }
                else
                {
                    if (!result.Contains(_activeRepository))
                        result.Add(_activeRepository);
                }
            }
        }
        else
        {
            result.Add(_activeRepository);
        }

        return result;
    }

    private class DateRangeVisitor : System.Linq.Expressions.ExpressionVisitor
    {
        public DateTime? StartDate { get; private set; }
        public DateTime? EndDate { get; private set; }

        protected override Expression VisitBinary(BinaryExpression node)
        {
            if (IsDateTimeComparison(node))
            {
                var dateValue = GetDateFromExpression(node.Right) ?? GetDateFromExpression(node.Left);
                if (dateValue.HasValue)
                {
                    switch (node.NodeType)
                    {
                        case ExpressionType.GreaterThan:
                        case ExpressionType.GreaterThanOrEqual:
                            StartDate = dateValue;
                            break;
                        case ExpressionType.LessThan:
                        case ExpressionType.LessThanOrEqual:
                            EndDate = dateValue;
                            break;
                        case ExpressionType.Equal:
                            StartDate = dateValue;
                            EndDate = dateValue;
                            break;
                    }
                }
            }

            return base.VisitBinary(node);
        }

        private bool IsDateTimeComparison(BinaryExpression node)
        {
            if (node.Left is MemberExpression leftMember)
            {
                var memberName = leftMember.Member.Name;
                if ((memberName == "CreatedAt" || memberName == "CreatedAtUtc" || memberName == "Id") &&
                    (node.Right.Type == typeof(DateTime) || node.Right.Type == typeof(DateTime?)))
                    return true;
            }
            if (node.Right is MemberExpression rightMember)
            {
                var memberName = rightMember.Member.Name;
                if ((memberName == "CreatedAt" || memberName == "CreatedAtUtc" || memberName == "Id") &&
                    (node.Left.Type == typeof(DateTime) || node.Left.Type == typeof(DateTime?)))
                    return true;
            }
            return false;
        }

        private DateTime? GetDateFromExpression(Expression expr)
        {
            while (expr.NodeType == ExpressionType.Convert)
            {
                expr = ((UnaryExpression)expr).Operand;
            }

            if (expr is ConstantExpression constExpr && constExpr.Value is DateTime dt)
            {
                return dt;
            }

            if (expr is MemberExpression memberExpr && memberExpr.Member is PropertyInfo prop &&
                prop.PropertyType == typeof(DateTime))
            {
                try
                {
                    return (DateTime?)prop.GetValue(null);
                }
                catch
                {
                    // Fallback
                }
            }
            return null;
        }

        protected override Expression VisitLambda<TLambda>(Expression<TLambda> node)
        {
            StartDate = null;
            EndDate = null;
            return base.VisitLambda(node);
        }
    }
}
