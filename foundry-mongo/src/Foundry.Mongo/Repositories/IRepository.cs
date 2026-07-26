using System.Linq.Expressions;
using Foundry.Core.Audit;
using Foundry.Core.Entities;
using Foundry.Core.Paging;
using Foundry.Core.Search;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Foundry.Mongo.Repositories;

/// <summary>Generic repository interface for a single entity type T. Provides all CRUD operations, pagination, and search capabilities with zero-boilerplate design.</summary>
public interface IRepository<T> where T : class, IEntity<ObjectId>
{
    /// <summary>MongoDB collection abstraction for the entity type.</summary>
    public IMongoCollection<T> Collection { get; }

    /// <summary>Gets the name of the MongoDB collection this repository targets.</summary>
    public string CollectionName { get; }

    // ─── Identity operations ──────────────────────────────────────────────

    /// <summary>Finds an entity by its unique ID. Returns null if not found. Uses $eq operator for optimal index usage.</summary>
    Task<T?> GetByIdAsync(object id, IClientSessionHandle? session = null, CancellationToken ct = default);

    // ─── Create operations ────────────────────────────────────────────────

    /// <summary>Inserts a single entity into the collection. Auto-stamps CreatedAtUtc and UpdatedAtUtc before writing. Triggers audit trail.</summary>
    Task InsertAsync(T entity, IClientSessionHandle? session = null, CancellationToken ct = default);

    /// <summary>Bulk inserts entities using MongoDB's BulkWriteAsync for optimal network round-trip performance. All entities are auto-stamped.</summary>
    Task BulkInsertAsync(IEnumerable<T> entities, IClientSessionHandle? session = null, CancellationToken ct = default);

    // ─── Read operations ──────────────────────────────────────────────────

    /// <summary>Finds all entities matching the filter expression with optional sorting. Returns up to limit items.</summary>
    Task<IReadOnlyList<T>> FindManyAsync(Expression<Func<T, bool>>? filter = null, string? sortBy = null, SortOrder sortOrder = SortOrder.Descending, int limit = 100, IClientSessionHandle? session = null, CancellationToken ct = default);

    /// <summary>Returns the total count of documents matching the filter using CountDocumentsAsync (not a full collection scan).</summary>
    Task<long> CountAsync(Expression<Func<T, bool>>? filter = null, IClientSessionHandle? session = null, CancellationToken ct = default);

    /// <summary>Gets or sets the maximum depth cap for offset pagination. Prevents performance degradation on deep page requests.</summary>
    int MaxDepthCap { get; set; }

    // ─── Paginations ──────────────────────────────────────────────────────

    /// <summary>Retrieves a paged resultset with metadata using either offset or cursor-based pagination. Uses CountDocuments for total count and aggregate pipeline for items.</summary>
    Task<PagedResult<T>> GetPagedAsync(PagedRequest request, Expression<Func<T, bool>>? filter = null, IClientSessionHandle? session = null, CancellationToken ct = default);

    /// <summary>Returns just the items on a page without count metadata (more efficient when you don't need totals).</summary>
    Task<IReadOnlyList<T>> GetPagedItemsAsync(PagedRequest request, Expression<Func<T, bool>>? filter = null, IClientSessionHandle? session = null, CancellationToken ct = default);

    /// <summary>Updates an entity by its ID. If the entity implements ISoftDelete and OperatorId is deleted, marks IsDeleted=true instead of removing it.</summary>
    Task UpdateByObjectIdAsync(object id, Func<T, T> updateSelector, string operatorId, IClientSessionHandle? session = null, CancellationToken ct = default);

    /// <summary>Performs a full replace update of an entity and generates an audit log entry.</summary>
    Task UpdateAsync(T entity, IClientSessionHandle? session = null, CancellationToken ct = default);

    /// <summary>Bulk updates entities matching a filter with a selector. Returns the count of updated documents. All are auto-stamped with UpdatedAtUtc.</summary>
    Task<IReadOnlyList<UpdateResult>> BulkUpdateManyAsync(Expression<Func<T, bool>> filter, Func<T, T> updateSelector, IClientSessionHandle? session = null, CancellationToken ct = default);

    /// <summary>Performs full replace updates for a batch of entities and generates audit log entries.</summary>
    Task BulkUpdateAsync(IEnumerable<T> entities, IClientSessionHandle? session = null, CancellationToken ct = default);

    /// <summary>Deletes an entity by its ID. If the entity implements ISoftDelete, sets IsDeleted=true and DeletedAt=UTC; otherwise performs hard delete.</summary>
    Task DeleteByObjectIdAsync(object id, string operatorId, IClientSessionHandle? session = null, CancellationToken ct = default);

    /// <summary>Deletes an entity by its ID. Respects soft-delete configuration.</summary>
    Task DeleteAsync(ObjectId id, IClientSessionHandle? session = null, CancellationToken ct = default);

    // ─── Dynamic Search ────────────────────────────────────────────────────

    /// <summary>Finds entities using dynamic search criteria that are compiled into strongly-typed Expression&lt;Func&lt;T, bool>> filters at runtime.</summary>
    Task<IReadOnlyList<T>> FindByCriteriaAsync(SearchCriterion[] criteria, IClientSessionHandle? session = null, CancellationToken ct = default);

    /// <summary>Performs a cursor-based search across heterogeneous collections project to UnifiedSearchResult using aggregation pipelines.</summary>
    Task<PagedResult<UnifiedSearchResult>> CrossCollectionSearchAsync(CrossCollectionSearchRequest request, IClientSessionHandle? session = null, CancellationToken ct = default);

    /// <summary>Searches using dynamic criteria with cursor-based pagination (preferred for large collections). Combines DynamicExpressionBuilder compilation with seek pagination.</summary>
    Task<PagedResult<T>> SearchPagedAsync(SearchCriterion[] criteria, PagedRequest pageRequest, IClientSessionHandle? session = null, CancellationToken ct = default);

    /// <summary>Automatically scans the entity properties for [Indexed] or [TextIndexed] attributes and creates corresponding indexes in MongoDB.</summary>
    Task CreateIndexesAsync(CancellationToken ct = default);

    /// <summary>Retrieves all historical revisions of a document by its ID, ordered by version descending.</summary>
    Task<IReadOnlyList<EntityRevision>> GetRevisionsAsync(object id, IClientSessionHandle? session = null, CancellationToken ct = default);

    /// <summary>Retrieves a specific historical revision of a document by its ID and version number.</summary>
    Task<EntityRevision?> GetRevisionByVersionAsync(object id, int version, IClientSessionHandle? session = null, CancellationToken ct = default);

    /// <summary>Restores a document to a specific historical version.</summary>
    Task<T> RestoreVersionAsync(object id, int version, IClientSessionHandle? session = null, CancellationToken ct = default);

    /// <summary>Returns a shallow clone of the entity with all properties marked with [SensitiveData] masked.</summary>
    T MaskSensitiveFields(T entity);

    /// <summary>Restores a soft-deleted entity back to active state. Clears IsDeleted and DeletedAt stamps.</summary>
    Task RestoreDeletedAsync(ObjectId id, IClientSessionHandle? session = null, CancellationToken ct = default);

    /// <summary>Runs a custom aggregation pipeline definition on the collection.</summary>
    Task<IReadOnlyList<TResult>> AggregateAsync<TResult>(PipelineDefinition<T, TResult> pipeline, IClientSessionHandle? session = null, CancellationToken ct = default);
}

// ─── Unified search and cross-collection types ──────────────────────────

/// <summary>Represents a unified search result from heterogeneous collections projected into a flattened model. Used by CrossCollectionSearchEngine for multi-collection queries that produce a common output schema.</summary>
public sealed record UnifiedSearchResult
{
    /// <summary>The original entity ID from the source document (as string representation).</summary>
    public required string EntityId { get; init; }

    /// <summary>The collection name this document originated from.</summary>
    public required string CollectionsName { get; init; }

    /// <summary>The fully qualified .NET type name of the entity.</summary>
    public required string EntityType { get; init; }

    /// <summary>Flattened property dictionary of the source MongoDB document. Includes all BSON fields as key-value pairs for search display/search highlighting.</summary>
    public Dictionary<string, object?> Properties { get; init; } = new();
}

/// <summary>Describes a cross-collection search by enumerating all collection names/aliases and the shared filter criteria applied across them.</summary>
public sealed record CrossCollectionSearchRequest
{
    /// <summary>List of entity types to search across each in the query. Each type must implement IEntity for consistent field resolution.</summary>
    public IReadOnlyList<Type> EntityTypes { get; init; } = [];

    /// <summary>Optional mapping between collection names and their corresponding entity types (e.g., "Orders" -> typeof(Order)).</summary>
    public Dictionary<string, Type>? CollectionToEntityTypeMap { get; init; }

    /// <summary>Shared search criteria applied to all collections in the query.</summary>
    public SearchCriterion[] Criteria { get; init; } = [];

    /// <summary>Pagination request for results. Uses cursor by default when available.</summary>
    public PagedRequest? Pagination { get; init; }

    /// <summary>Maximum number of properties to project per document (0 = all). Helps reduce memory footprint for large documents.</summary>
    public int MaxPropertyCount { get; init; }
}
