using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Foundry.Core.Attributes;
using Foundry.Core.Entities;
using Humanizer;

namespace Foundry.Mongo.Services;

/// <summary>
/// Background worker that runs periodically to archive records older than the configured threshold years
/// from active collections to year-based archive collections.
/// </summary>
public sealed class DataArchivalWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IMongoDatabase _database;
    private readonly ILogger<DataArchivalWorker> _logger;

    public DataArchivalWorker(IServiceProvider serviceProvider, IMongoDatabase database, ILogger<DataArchivalWorker> logger)
    {
        _serviceProvider = serviceProvider;
        _database = database;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Data Archival Background Worker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Run archival sweep
                await RunSweepAsync(stoppingToken);

                // Sleep for 24 hours
                await Task.Delay(TimeSpan.FromHours(24), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An error occurred during hot-to-cold data archiving. Retrying in 1 hour.");
                try
                {
                    await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        _logger.LogInformation("Data Archival Background Worker stopped.");
    }

    /// <summary>
    /// Runs one archival sweep across every partitioned entity type.
    /// </summary>
    /// <remarks>
    /// Public so a sweep can be triggered on demand — after a bulk import, or by an operator who does
    /// not want to wait a day — and so it can be tested. The background loop calls this and nothing
    /// else, so what runs on a schedule is what a test exercises.
    /// </remarks>
    public async Task RunSweepAsync(CancellationToken cancellationToken = default)
    {
        var partitionedTypes = GetPartitionedTypes();
        _logger.LogInformation("Found {Count} partitioned entity types to sweep.", partitionedTypes.Count);

        // Every type is attempted, and the sweep still reports failure. One entity that cannot be
        // archived must not silently block the rest -- nor be reported as a clean run.
        var failures = new List<Exception>();

        foreach (var type in partitionedTypes)
        {
            try
            {
                await ProcessPartitionedTypeAsync(type, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to archive entity: {Name}", type.Name);
                failures.Add(ex);
            }
        }

        if (failures.Count > 0)
        {
            // Flattened, because a per-year failure arrives here already wrapped: an operator should
            // read the causes, not unwrap two layers of AggregateException to reach them.
            throw new AggregateException(
                $"{failures.Count} of {partitionedTypes.Count} partitioned entity types could not be archived.",
                failures).Flatten();
        }
    }

    private List<Type> GetPartitionedTypes()
    {
        var entityInterface = typeof(IEntity<ObjectId>);
        var attributeType = typeof(PartitionedAttribute);

        return AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => {
                try { return a.GetTypes(); }
                catch { return Array.Empty<Type>(); }
            })
            .Where(t => entityInterface.IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface)
            .Where(t => t.GetCustomAttribute(attributeType) != null)
            .ToList();
    }

    private async Task ProcessPartitionedTypeAsync(Type entityType, CancellationToken cancellationToken)
    {
        var attr = entityType.GetCustomAttribute<PartitionedAttribute>();
        if (attr == null) return;

        var pluralName = entityType.Name.Pluralize();
        _logger.LogInformation("Sweeping partitioned entity: {Name} (Threshold: {Years} years)", entityType.Name, attr.ArchiveThresholdYears);

        var collection = _database.GetCollection<BsonDocument>(pluralName);
        var archiveThresholdDate = DateTime.UtcNow.AddYears(-attr.ArchiveThresholdYears);
        var thresholdId = ObjectId.GenerateNewId(archiveThresholdDate);

        // Documents older than the threshold, by the timestamp embedded in their ObjectId.
        var filter = Builders<BsonDocument>.Filter.Lt("_id", thresholdId);

        var oldDocuments = await collection.Find(filter).ToListAsync(cancellationToken);
        if (oldDocuments.Count == 0)
        {
            _logger.LogInformation("No old documents found to archive for entity: {Name}", entityType.Name);
            return;
        }

        _logger.LogInformation("Archiving {Count} documents for entity: {Name}", oldDocuments.Count, entityType.Name);

        var useTransaction = await SupportsTransactionsAsync(cancellationToken);

        // Each year is independent -- a separate archive collection, its own documents -- so one that
        // cannot be archived must not strand the others, and must still be reported. Without this the
        // first bad year abandoned every year after it, which is the rule the sweep already applies
        // across entity types and had not applied within one. Selection filters on _id, so years
        // arrive oldest first: the abandoned ones were the *newer* years, the likeliest to matter.
        var failures = new List<Exception>();

        foreach (var group in oldDocuments.GroupBy(d => d["_id"].AsObjectId.CreationTime.Year))
        {
            try
            {
                await ArchiveYearAsync(collection, pluralName, group.Key, group.ToList(), useTransaction, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to archive {Name} for {Year}.", entityType.Name, group.Key);
                failures.Add(ex);
            }
        }

        if (failures.Count > 0)
        {
            throw new AggregateException(
                $"{failures.Count} archive year(s) of '{pluralName}' could not be archived.", failures);
        }
    }

    /// <summary>
    /// Moves one year's documents into their archive collection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Insert then delete, in that order, and <b>never</b> the reverse. Without a transaction a
    /// failure between the two duplicates a document into the archive, which the copy-then-verify
    /// below detects and which a re-run corrects. Deleting first would lose it outright.
    /// </para>
    /// <para>
    /// The whole sweep used to run inside <c>WithTransactionAsync</c>, and MongoDB supports
    /// multi-document transactions only on a replica set. Every deployment this project ships --
    /// its own docker-compose, and the `mongo:7` service in CI -- is a standalone server, so the
    /// sweep threw <c>NotSupportedException: Standalone servers do not support transactions</c> on
    /// every run. The exception was caught and logged, so **archival never happened and nothing
    /// said so**: records aged past the threshold, stayed in the active collection, and became
    /// unreachable, because reads route by id age and never look there.
    /// </para>
    /// </remarks>
    private async Task ArchiveYearAsync(
        IMongoCollection<BsonDocument> active,
        string pluralName,
        int year,
        List<BsonDocument> documents,
        bool useTransaction,
        CancellationToken ct)
    {
        var archiveCollectionName = $"{pluralName}_{year}";
        var archiveCollection = _database.GetCollection<BsonDocument>(archiveCollectionName);
        var ids = documents.Select(d => d["_id"].AsObjectId).ToList();
        var byId = Builders<BsonDocument>.Filter.In("_id", ids);

        if (useTransaction)
        {
            using var session = await _database.Client.StartSessionAsync(cancellationToken: ct);
            await session.WithTransactionAsync(async (st, token) =>
            {
                await archiveCollection.InsertManyAsync(st, documents, cancellationToken: token);
                await active.DeleteManyAsync(st, byId, cancellationToken: token);
                return true;
            }, cancellationToken: ct);

            _logger.LogInformation(
                "Archived {Count} documents into {ArchiveName} transactionally.", documents.Count, archiveCollectionName);
            return;
        }

        // Unordered, so one already-archived document from an interrupted previous run does not stop
        // the rest. A duplicate key here means the copy already exists, which is the outcome wanted.
        try
        {
            await archiveCollection.InsertManyAsync(
                documents, new InsertManyOptions { IsOrdered = false }, ct);
        }
        catch (MongoBulkWriteException<BsonDocument> ex)
            when (ex.WriteErrors.All(e => e.Category == ServerErrorCategory.DuplicateKey))
        {
            _logger.LogInformation(
                "{Count} documents were already present in {ArchiveName} from an earlier run.",
                ex.WriteErrors.Count, archiveCollectionName);
        }

        // Deleted only once the archive copy is confirmed present. Without a transaction this is
        // what stands between an interrupted sweep and data loss.
        var archivedCount = await archiveCollection.CountDocumentsAsync(byId, cancellationToken: ct);
        if (archivedCount != documents.Count)
        {
            throw new InvalidOperationException(
                $"Archiving '{pluralName}' into '{archiveCollectionName}' copied {archivedCount} of "
                + $"{documents.Count} documents. The active collection is left untouched so nothing "
                + "is lost; re-run the sweep once the cause is resolved.");
        }

        await active.DeleteManyAsync(byId, ct);

        _logger.LogInformation(
            "Archived {Count} documents into {ArchiveName}.", documents.Count, archiveCollectionName);
    }

    /// <summary>
    /// Whether this deployment can run a multi-document transaction.
    /// </summary>
    /// <remarks>
    /// A replica set or sharded cluster can; a standalone server cannot. Determined from the server's
    /// own description rather than by attempting a transaction and catching the failure, so that a
    /// genuine transaction error is never mistaken for "this server does not do transactions".
    /// </remarks>
    private async Task<bool> SupportsTransactionsAsync(CancellationToken ct)
    {
        try
        {
            var hello = await _database.RunCommandAsync<BsonDocument>(
                new BsonDocument("hello", 1), cancellationToken: ct);

            var isReplicaSet = hello.Contains("setName");
            var isSharded = hello.GetValue("msg", BsonNull.Value).ToString() == "isdbgrid";

            if (!isReplicaSet && !isSharded)
            {
                _logger.LogWarning(
                    "MongoDB is a standalone server, which does not support multi-document "
                    + "transactions. Archival will copy and verify before deleting instead. Run a "
                    + "replica set in production so a sweep is atomic.");
            }

            return isReplicaSet || isSharded;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not determine whether MongoDB supports transactions; assuming it does not.");
            return false;
        }
    }
}
