using System.Collections.Concurrent;
using System.Reflection;
using Foundry.Core.Entities;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Foundry.Mongo.Services;

/// <summary>
/// Internal service responsible for scanning entity properties for <see cref="IndexedAttribute"/>
/// and <see cref="TextIndexedAttribute"/> and creating the corresponding MongoDB indexes.
/// Caches the scanned index models per entity type to avoid repeated reflection.
/// </summary>
internal sealed class EntityIndexManager<T> where T : class, IEntity<ObjectId>
{
    private readonly IMongoCollection<T> _collection;

    /// <summary>
    /// Cached index models per entity type. Computed once per type and reused across calls.
    /// </summary>
    private static readonly ConcurrentDictionary<Type, IReadOnlyList<CreateIndexModel<T>>> _indexModelsCache = new();

    public EntityIndexManager(IMongoCollection<T> collection)
    {
        _collection = collection ?? throw new ArgumentNullException(nameof(collection));
    }

    /// <summary>
    /// Scans entity properties for <see cref="IndexedAttribute"/> and <see cref="TextIndexedAttribute"/>,
    /// builds the corresponding index models, and creates them on the MongoDB collection.
    /// </summary>
    internal async Task CreateIndexesAsync(CancellationToken ct = default)
    {
        var indexModels = _indexModelsCache.GetOrAdd(typeof(T), _ => BuildIndexModels());

        if (indexModels.Count > 0)
        {
            await _collection.Indexes.CreateManyAsync(indexModels, null, ct);
        }
    }

    /// <summary>
    /// Builds index models by scanning entity properties for index attributes.
    /// </summary>
    private static IReadOnlyList<CreateIndexModel<T>> BuildIndexModels()
    {
        var properties = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var indexModels = new List<CreateIndexModel<T>>();
        var textIndexFields = new List<string>();

        foreach (var prop in properties)
        {
            if (!prop.CanRead) continue;

            // Check for [Indexed]
            var indexedAttr = prop.GetCustomAttribute<IndexedAttribute>();
            if (indexedAttr != null)
            {
                var indexKeys = indexedAttr.Descending
                    ? Builders<T>.IndexKeys.Descending(prop.Name)
                    : Builders<T>.IndexKeys.Ascending(prop.Name);

                var options = new CreateIndexOptions
                {
                    Unique = indexedAttr.Unique,
                    Name = indexedAttr.Name
                };

                indexModels.Add(new CreateIndexModel<T>(indexKeys, options));
            }

            // Check for [TextIndexed]
            var textIndexedAttr = prop.GetCustomAttribute<TextIndexedAttribute>();
            if (textIndexedAttr != null)
            {
                textIndexFields.Add(prop.Name);
            }
        }

        // Type-level compound indexes. These carry field order, which per-property attributes
        // cannot express, and are the only representation of the IR's entity-level 'indexes'.
        foreach (var compound in typeof(T).GetCustomAttributes<CompoundIndexAttribute>(inherit: true))
        {
            if (compound.Fields is not { Length: > 0 }) continue;

            IndexKeysDefinition<T>? keys = null;
            foreach (var field in compound.Fields)
            {
                if (string.IsNullOrWhiteSpace(field)) continue;

                var next = Builders<T>.IndexKeys.Ascending(field);
                keys = keys is null ? next : Builders<T>.IndexKeys.Combine(keys, next);
            }

            if (keys is null) continue;

            indexModels.Add(new CreateIndexModel<T>(keys, new CreateIndexOptions
            {
                Unique = compound.Unique,
                // A stable derived name keeps a rebuild from creating a second copy of the same
                // index under a driver-generated name.
                Name = string.IsNullOrWhiteSpace(compound.Name)
                    ? "IX_" + string.Join("_", compound.Fields)
                    : compound.Name
            }));
        }

        if (textIndexFields.Count > 0)
        {
            IndexKeysDefinition<T>? textKeys = null;
            foreach (var field in textIndexFields)
            {
                if (textKeys == null)
                    textKeys = Builders<T>.IndexKeys.Text(field);
                else
                    textKeys = Builders<T>.IndexKeys.Combine(textKeys, Builders<T>.IndexKeys.Text(field));
            }

            if (textKeys != null)
            {
                indexModels.Add(new CreateIndexModel<T>(textKeys, new CreateIndexOptions { Name = "TextIndex" }));
            }
        }

        return indexModels;
    }
}
