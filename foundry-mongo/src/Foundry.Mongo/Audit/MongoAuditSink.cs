using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Foundry.Core.Audit;
using MongoDB.Driver;

namespace Foundry.Mongo.Audit;

/// <summary>
/// Appends audit log entries to a MongoDB collection for durable, distributed audit trail storage.
/// This sink never updates or deletes entries — entries are immutable once written.
/// Making the collection genuinely immutable (capped collection, time-series collection, or role-based access
/// control without remove privileges) is a deployment concern this class cannot enforce; it is the responsibility
/// of MongoDB administrators and application configuration.
/// </summary>
public sealed class MongoAuditSink : IAuditSink, IDisposable
{
    private readonly IMongoDatabase _database;
    private readonly string _collectionName;
    private readonly SemaphoreSlim _indexSemaphore = new(1, 1);
    private volatile bool _indexesCreated;

    /// <summary>
    /// Initializes a new instance of the <see cref="MongoAuditSink"/> class.
    /// </summary>
    /// <param name="database">The MongoDB database instance where audit logs will be stored.</param>
    /// <param name="collectionName">The name of the collection to write audit entries to. Defaults to "audit_log".</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="database"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="collectionName"/> is null or whitespace.</exception>
    public MongoAuditSink(IMongoDatabase database, string collectionName = "audit_log")
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));

        if (string.IsNullOrWhiteSpace(collectionName))
            throw new ArgumentException("Collection name is required and cannot be null or whitespace.", nameof(collectionName));

        _collectionName = collectionName;
    }

    /// <inheritdoc />
    public async Task WriteAsync(AuditLogEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        CheckConfiguration(entry);

        var collection = _database.GetCollection<AuditLogEntry>(_collectionName);
        await EnsureIndexesAsync(ct);
        await collection.InsertOneAsync(entry, cancellationToken: ct);
    }

    /// <inheritdoc />
    public async Task WriteManyAsync(IReadOnlyList<AuditLogEntry> entries, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entries);

        // Return early without touching Mongo when list is empty
        if (entries.Count == 0)
            return;

        // Validate ALL entries before inserting ANY of them (all-or-nothing batch consistency)
        foreach (var entry in entries)
            CheckConfiguration(entry);

        var collection = _database.GetCollection<AuditLogEntry>(_collectionName);
        await EnsureIndexesAsync(ct);
        await collection.InsertManyAsync(entries, cancellationToken: ct);
    }

    private static void CheckConfiguration(AuditLogEntry entry)
    {
        if (string.IsNullOrEmpty(entry.OperatorId))
            throw new InvalidOperationException("Audit sink requires a configured OperatorId in AuditLogEntry");

        if (string.IsNullOrEmpty(entry.EntityType))
            throw new InvalidOperationException("Audit log entry must include entity type for audit purposes");
    }

    private async Task EnsureIndexesAsync(CancellationToken ct)
    {
        if (_indexesCreated)
            return;

        // Use a semaphore to ensure indexes are created exactly once, even under concurrent writes.
        // This allows async/await coordination without blocking threads on network I/O.
        await _indexSemaphore.WaitAsync(ct);
        try
        {
            // Double-check after acquiring the semaphore, in case another task already created the indexes
            if (_indexesCreated)
                return;

            var collection = _database.GetCollection<AuditLogEntry>(_collectionName);
            var indexManager = collection.Indexes;

            try
            {
                // Compound index: EntityType, EntityId (ascending), TimestampUtc (descending)
                // Optimizes queries filtering by entity type + id with newest entries first
                var compoundIndexModel = new CreateIndexModel<AuditLogEntry>(
                    Builders<AuditLogEntry>.IndexKeys
                        .Ascending(e => e.EntityType)
                        .Ascending(e => e.EntityId)
                        .Descending(e => e.TimestampUtc));

                // Single index: TimestampUtc (descending)
                // Optimizes time-based queries (e.g., recent audit activity)
                var timestampIndexModel = new CreateIndexModel<AuditLogEntry>(
                    Builders<AuditLogEntry>.IndexKeys
                        .Descending(e => e.TimestampUtc));

                await indexManager.CreateManyAsync(
                    new[] { compoundIndexModel, timestampIndexModel },
                    cancellationToken: ct);
            }
            catch
            {
                // Index creation is a queryability optimization; if it fails (e.g., insufficient privileges,
                // ephemeral collection), the audit path must not break. Swallow the exception silently.
                // One broken index concern must not cascade into broken audit writes, which would corrupt
                // the very audit trail that exists to detect such failures.
            }
            finally
            {
                // Set the flag even when index creation throws. If creation fails due to permissions or
                // other permanent conditions, retrying it on every single write would turn a one-time
                // deployment problem into a per-write network round-trip that destroys performance. Only
                // the first write pays the cost; subsequent writes skip it.
                _indexesCreated = true;
            }
        }
        finally
        {
            _indexSemaphore.Release();
        }
    }

    /// <summary>
    /// Disposes resources held by this audit sink.
    /// </summary>
    public void Dispose()
    {
        _indexSemaphore?.Dispose();
    }
}
