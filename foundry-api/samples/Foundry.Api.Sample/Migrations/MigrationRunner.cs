using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MongoDB.Bson;
using MongoDB.Driver;
using Microsoft.Extensions.Logging;

namespace Foundry.Api.Sample.Migrations;

public class MigrationRunner
{
    private readonly IMongoDatabase _database;
    private readonly IEnumerable<DatabaseMigration> _migrations;
    private readonly ILogger<MigrationRunner> _logger;

    public MigrationRunner(
        IMongoDatabase database,
        IEnumerable<DatabaseMigration> migrations,
        ILogger<MigrationRunner> logger)
    {
        _database = database;
        _migrations = migrations;
        _logger = logger;
    }

    public async Task RunPendingAsync(CancellationToken ct)
    {
        _logger.LogInformation("Checking database schema migrations...");

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            await _database.RunCommandAsync((Command<BsonDocument>)"{ping:1}", cancellationToken: cts.Token);
        }
        catch (Exception)
        {
            _logger.LogWarning("MongoDB connection is not available. Skipping startup schema migrations.");
            return;
        }
        
        var history = _database.GetCollection<BsonDocument>("SchemaHistory");
        
        // Ensure index on Version
        await history.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("Version"),
                new CreateIndexOptions { Unique = true }
            ),
            cancellationToken: ct
        );

        var currentVersionDoc = await history.Find(new BsonDocument())
            .SortByDescending(d => d["Version"])
            .FirstOrDefaultAsync(ct);
            
        int currentVersion = currentVersionDoc != null ? currentVersionDoc["Version"].AsInt32 : 0;
        
        var pendingMigrations = _migrations
            .Where(m => m.Version > currentVersion)
            .OrderBy(m => m.Version)
            .ToList();

        if (pendingMigrations.Count == 0)
        {
            _logger.LogInformation("Database is up to date (Version {Version}).", currentVersion);
            return;
        }

        foreach (var migration in pendingMigrations)
        {
            _logger.LogWarning("Running migration to version {Version}...", migration.Version);
            
            await migration.UpAsync(_database, ct);

            await history.ReplaceOneAsync(
                Builders<BsonDocument>.Filter.Eq("Version", migration.Version),
                new BsonDocument
                {
                    { "Version", migration.Version },
                    { "AppliedAt", DateTime.UtcNow }
                },
                new ReplaceOptions { IsUpsert = true },
                cancellationToken: ct
            );
            
            _logger.LogInformation("Migration to version {Version} completed successfully.", migration.Version);
        }
    }
}
