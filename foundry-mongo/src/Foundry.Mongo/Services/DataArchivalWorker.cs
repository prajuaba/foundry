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
                await ArchiveOldDataAsync(stoppingToken);

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

    private async Task ArchiveOldDataAsync(CancellationToken cancellationToken)
    {
        var partitionedTypes = GetPartitionedTypes();
        _logger.LogInformation("Found {Count} partitioned entity types to sweep.", partitionedTypes.Count);

        foreach (var type in partitionedTypes)
        {
            await ProcessPartitionedTypeAsync(type, cancellationToken);
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

        try
        {
            var collection = _database.GetCollection<BsonDocument>(pluralName);
            var archiveThresholdDate = DateTime.UtcNow.AddYears(-attr.ArchiveThresholdYears);
            var dummyId = ObjectId.GenerateNewId(archiveThresholdDate);

            // Filter for documents older than threshold using ObjectId creation timestamp
            var filter = Builders<BsonDocument>.Filter.Lt("_id", dummyId);

            var oldDocuments = await collection.Find(filter).ToListAsync(cancellationToken);
            if (!oldDocuments.Any())
            {
                _logger.LogInformation("No old documents found to archive for entity: {Name}", entityType.Name);
                return;
            }

            _logger.LogInformation("Archiving {Count} documents for entity: {Name}", oldDocuments.Count, entityType.Name);

            // Group by creation year
            var groupedByYear = oldDocuments.GroupBy(d => d["_id"].AsObjectId.CreationTime.Year);

            using var session = await _database.Client.StartSessionAsync(cancellationToken: cancellationToken);
            await session.WithTransactionAsync(async (sessionToken, ct) =>
            {
                foreach (var group in groupedByYear)
                {
                    var year = group.Key;
                    var archiveCollectionName = $"{pluralName}_{year}";
                    var archiveCollection = _database.GetCollection<BsonDocument>(archiveCollectionName);

                    var docsList = group.ToList();

                    // 1. Write to Archive Collection
                    await archiveCollection.InsertManyAsync(sessionToken, docsList, cancellationToken: ct);

                    // 2. Delete from Active Collection
                    var ids = docsList.Select(d => d["_id"].AsObjectId).ToList();
                    var deleteFilter = Builders<BsonDocument>.Filter.In("_id", ids);
                    await collection.DeleteManyAsync(sessionToken, deleteFilter, cancellationToken: ct);

                    _logger.LogInformation("Successfully archived {Count} documents into {ArchiveName}.", docsList.Count, archiveCollectionName);
                }
                return true;
            }, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to run archiving migration for entity: {Name}", entityType.Name);
        }
    }
}
