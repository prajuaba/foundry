using Foundry.Core.Entities;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Foundry.Mongo.Services;

/// <summary>
/// Internal service responsible for managing entity revision history in shadow "_History" collections.
/// Handles saving snapshots, retrieving revision lists, fetching specific versions, and saving bulk revisions.
/// </summary>
internal sealed class EntityVersioningService<T> where T : class, IEntity<ObjectId>
{
    private readonly IMongoDatabase _database;

    public EntityVersioningService(IMongoDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    /// <summary>
    /// Gets the history collection for the given base collection name.
    /// </summary>
    private IMongoCollection<EntityRevision> GetHistoryCollection(string collectionName)
    {
        return _database.GetCollection<EntityRevision>(collectionName + "_History");
    }

    /// <summary>
    /// Saves a single entity snapshot to the history collection.
    /// </summary>
    internal async Task SaveRevisionAsync(
        string collectionName,
        string entityId,
        int version,
        BsonDocument data,
        string changedBy,
        string action,
        IClientSessionHandle? session = null,
        CancellationToken ct = default)
    {
        var historyCollection = GetHistoryCollection(collectionName);
        var revision = new EntityRevision
        {
            EntityId = entityId,
            Version = version,
            Data = data,
            ChangedBy = changedBy,
            Action = action
        };

        if (session != null)
            await historyCollection.InsertOneAsync(session, revision, null, ct);
        else
            await historyCollection.InsertOneAsync(revision, null, ct);
    }

    /// <summary>
    /// Saves multiple entity snapshots to the history collection in a single batch.
    /// </summary>
    internal async Task SaveRevisionsAsync(
        string collectionName,
        IReadOnlyList<EntityRevision> revisions,
        IClientSessionHandle? session = null,
        CancellationToken ct = default)
    {
        if (revisions.Count == 0) return;

        var historyCollection = GetHistoryCollection(collectionName);
        if (session != null)
            await historyCollection.InsertManyAsync(session, revisions, null, ct);
        else
            await historyCollection.InsertManyAsync(revisions, null, ct);
    }

    /// <summary>
    /// Retrieves all historical revisions of a document by its entity ID string, ordered by version descending.
    /// </summary>
    internal async Task<IReadOnlyList<EntityRevision>> GetRevisionsAsync(
        string collectionName,
        string entityIdStr,
        IClientSessionHandle? session = null,
        CancellationToken ct = default)
    {
        var historyCollection = GetHistoryCollection(collectionName);
        var filter = Builders<EntityRevision>.Filter.Eq(r => r.EntityId, entityIdStr);
        var sort = Builders<EntityRevision>.Sort.Descending(r => r.Version);

        var findOptions = new FindOptions<EntityRevision> { Sort = sort };
        var cursor = session != null
            ? await historyCollection.FindAsync(session, filter, findOptions, ct)
            : await historyCollection.FindAsync(filter, findOptions, ct);

        return await cursor.ToListAsync(ct);
    }

    /// <summary>
    /// Retrieves a specific historical revision of a document by its entity ID string and version number.
    /// </summary>
    internal async Task<EntityRevision?> GetRevisionByVersionAsync(
        string collectionName,
        string entityIdStr,
        int version,
        IClientSessionHandle? session = null,
        CancellationToken ct = default)
    {
        var historyCollection = GetHistoryCollection(collectionName);
        var filter = Builders<EntityRevision>.Filter.And(
            Builders<EntityRevision>.Filter.Eq(r => r.EntityId, entityIdStr),
            Builders<EntityRevision>.Filter.Eq(r => r.Version, version)
        );

        var findOptions = new FindOptions<EntityRevision> { Limit = 1 };
        var cursor = session != null
            ? await historyCollection.FindAsync(session, filter, findOptions, ct)
            : await historyCollection.FindAsync(filter, findOptions, ct);

        return await cursor.FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Creates an <see cref="EntityRevision"/> object for batching (without saving).
    /// </summary>
    internal static EntityRevision CreateRevision(
        string entityId,
        int version,
        BsonDocument data,
        string changedBy,
        string action)
    {
        return new EntityRevision
        {
            EntityId = entityId,
            Version = version,
            Data = data,
            ChangedBy = changedBy,
            Action = action
        };
    }
}
