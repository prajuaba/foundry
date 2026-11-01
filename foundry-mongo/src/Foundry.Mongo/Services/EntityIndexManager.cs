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
            if (IsMultiTenant) await DropGlobalUniqueIndexesAsync(indexModels, ct);
            await DropRedeclaredIndexesAsync(indexModels, ct);
            await _collection.Indexes.CreateManyAsync(indexModels, null, ct);
        }
    }

    private static bool IsMultiTenant => typeof(Foundry.Core.Tenant.IMultiTenant).IsAssignableFrom(typeof(T));

    private const string TenantField = nameof(Foundry.Core.Tenant.IMultiTenant.TenantId);

    /// <summary>
    /// Drops a unique index left from before uniqueness was scoped per tenant, so it does not go on
    /// enforcing uniqueness across every tenant beside the scoped one that replaces it.
    /// </summary>
    /// <remarks>
    /// Only an index whose keys are exactly a scoped index's keys minus the leading tenant is dropped:
    /// that is the index this one replaces, and nothing else here is touched. Replacing a global unique
    /// index with a per-tenant one only loosens it, so the create that follows cannot fail on data.
    /// </remarks>
    private async Task DropGlobalUniqueIndexesAsync(IReadOnlyList<CreateIndexModel<T>> models, CancellationToken ct)
    {
        var args = new RenderArgs<T>(_collection.DocumentSerializer, _collection.Settings.SerializerRegistry);
        var replaced = models
            .Where(m => m.Options?.Unique == true)
            .Select(m => m.Keys.Render(args))
            .Where(keys => keys.ElementCount > 1)
            .Select(keys => new BsonDocument(keys.Elements.Skip(1)))
            .ToList();
        if (replaced.Count == 0) return;

        using var cursor = await _collection.Indexes.ListAsync(ct);
        foreach (var existing in await cursor.ToListAsync(ct))
        {
            if (!existing.GetValue("unique", false).ToBoolean()) continue;
            if (existing.GetValue("key", null) is not BsonDocument key) continue;
            if (!replaced.Any(r => r.Equals(key))) continue;

            await _collection.Indexes.DropOneAsync(existing["name"].AsString, ct);
        }
    }

    /// <summary>
    /// Drops an index whose declaration has changed since it was created, so it is created again
    /// as declared.
    /// </summary>
    /// <remarks>
    /// Creating an index under a name the collection already holds with other keys, another
    /// uniqueness or another partial filter fails, so an application whose schema changed an index
    /// would not start -- and a renamed one would leave the old index enforcing beside it. Found when a
    /// unique link index gained two fields: under the old keys, a project could hold only one link of
    /// the new kind. A declared name is matched by name; an unnamed declaration by the driver's
    /// default name for its keys, so an index someone added by hand under another name is never
    /// touched. A changed index that is now stricter than the data allows still fails on create,
    /// loudly, as a new one would. A text index is never compared: the server stores it under
    /// different keys from the ones declared, so it would be dropped and rebuilt on every start.
    /// </remarks>
    private async Task DropRedeclaredIndexesAsync(IReadOnlyList<CreateIndexModel<T>> models, CancellationToken ct)
    {
        var args = new RenderArgs<T>(_collection.DocumentSerializer, _collection.Settings?.SerializerRegistry ?? MongoDB.Bson.Serialization.BsonSerializer.SerializerRegistry);
        var declared = new Dictionary<string, (BsonDocument Keys, bool Unique, BsonDocument? Partial)>();
        foreach (var m in models)
        {
            var keys = m.Keys.Render(args);
            if (keys.Values.Any(v => v.IsString && v.AsString == "text")) continue;
            var name = string.IsNullOrWhiteSpace(m.Options?.Name) ? DefaultName(keys) : m.Options!.Name!;
            var partial = (m.Options as CreateIndexOptions<T>)?.PartialFilterExpression?.Render(args);
            declared[name] = (keys, m.Options?.Unique == true, partial);
        }
        if (declared.Count == 0) return;

        using var cursor = await _collection.Indexes.ListAsync(ct);
        if (cursor == null) return;
        foreach (var existing in await cursor.ToListAsync(ct))
        {
            var name = existing.GetValue("name", BsonNull.Value);
            if (!name.IsString || !declared.TryGetValue(name.AsString, out var wanted)) continue;
            var sameKeys = existing.GetValue("key", null) is BsonDocument key && key.Equals(wanted.Keys);
            var sameUnique = existing.GetValue("unique", false).ToBoolean() == wanted.Unique;
            var samePartial = Equals(existing.GetValue("partialFilterExpression", null) as BsonDocument, wanted.Partial);
            if (sameKeys && sameUnique && samePartial) continue;

            await _collection.Indexes.DropOneAsync(name.AsString, ct);
        }
    }

    /// <summary>The name the server gives an index created without one: <c>field_1_other_-1</c>.</summary>
    private static string DefaultName(BsonDocument keys)
        => string.Join("_", keys.Elements.Select(e => $"{e.Name}_{e.Value}"));

    private static bool IsSoftDelete => typeof(ISoftDelete).IsAssignableFrom(typeof(T));

    /// <summary>
    /// The options for a declared index. A unique index on a soft-deleted entity counts live rows only.
    /// </summary>
    /// <remarks>
    /// A deleted row stays in the collection, and it used to keep its unique value for good: a removed
    /// squad member could never rejoin, a deleted code or name could never be used again, and every
    /// attempt was refused as a duplicate of a row the API no longer shows. The index now holds only
    /// rows whose <c>IsDeleted</c> is false, so a deleted row gives its value up. Restoring it is then
    /// refused as a duplicate if a live row has taken the value since -- the same refusal a create
    /// gets, from the same index. The field is always written on these entities, so no live row is
    /// missing from the index.
    /// </remarks>
    private static CreateIndexOptions<T> Options(bool unique, string? name) => new()
    {
        Unique = unique,
        Name = name,
        PartialFilterExpression = unique && IsSoftDelete
            ? Builders<T>.Filter.Eq(nameof(ISoftDelete.IsDeleted), false)
            : null
    };

    /// <summary>
    /// A unique index on a multi-tenant entity is unique within its tenant.
    /// </summary>
    /// <remarks>
    /// The keys were taken as declared, so every unique index was global: a second tenant could not
    /// create a project whose code another tenant already used, and the 409 that refused it told them
    /// the code existed somewhere else. Isolation must not depend on how an index was declared.
    /// </remarks>
    private static IndexKeysDefinition<T> ScopedToTenant(IndexKeysDefinition<T> keys, bool unique, string firstField)
        => unique && IsMultiTenant && !string.Equals(firstField, TenantField, StringComparison.Ordinal)
            ? Builders<T>.IndexKeys.Combine(Builders<T>.IndexKeys.Ascending(TenantField), keys)
            : keys;

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

                var options = Options(indexedAttr.Unique, indexedAttr.Name);

                indexModels.Add(new CreateIndexModel<T>(ScopedToTenant(indexKeys, indexedAttr.Unique, prop.Name), options));
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

            keys = ScopedToTenant(keys, compound.Unique, compound.Fields[0]);
            // A stable derived name keeps a rebuild from creating a second copy of the same
            // index under a driver-generated name.
            indexModels.Add(new CreateIndexModel<T>(keys, Options(compound.Unique,
                string.IsNullOrWhiteSpace(compound.Name) ? "IX_" + string.Join("_", compound.Fields) : compound.Name)));
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
