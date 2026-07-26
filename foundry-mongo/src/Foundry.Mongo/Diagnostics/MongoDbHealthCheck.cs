using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Foundry.Mongo.Diagnostics;

/// <summary>
/// Monitors connection health by running a fast, lightweight ping command against the target IMongoDatabase.
/// Suitable for containerized cloud deployment health checks (Kubernetes liveness/readiness probes).
/// </summary>
public sealed class MongoDbHealthCheck : IHealthCheck
{
    private readonly IMongoDatabase _database;

    public MongoDbHealthCheck(IMongoDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Send a ping command to verify the db connection roundtrip
            await _database.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), null, cancellationToken);
            return HealthCheckResult.Healthy("MongoDB connection is active and responding to ping.");
        }
        catch (Exception ex)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, "MongoDB ping operation failed.", ex);
        }
    }
}
