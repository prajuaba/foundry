using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Foundry.Mongo.Diagnostics;

/// <summary>
/// Monitors connection health of the MongoDB instance by running a lightweight ping command against the target IMongoDatabase.
/// Suitable for containerized cloud deployment health checks (Kubernetes liveness/readiness probes).
/// </summary>
public sealed class MongoHealthCheck : IHealthCheck
{
    private readonly IMongoDatabase _database;
    private readonly TimeSpan _timeout;

    /// <summary>
    /// Initializes a new instance of the <see cref="MongoHealthCheck"/> class.
    /// </summary>
    /// <param name="database">The MongoDB database instance to perform the health check against.</param>
    /// <param name="timeout">The maximum duration to wait for the ping command. Defaults to 3 seconds.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="database"/> is null.</exception>
    public MongoHealthCheck(IMongoDatabase database, TimeSpan? timeout = null)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _timeout = timeout ?? TimeSpan.FromSeconds(3);
    }

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Link the passed cancellation token to a timeout-bounded token so that the ping command
            // cannot exceed the configured timeout, even if the caller passes a long-lived token.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(_timeout);

            // Query MongoDB with a ping command to verify connectivity.
            // Unlike KafkaHealthCheck (which blocks on a sync API with no async counterpart), the MongoDB
            // driver is async-native. Awaiting here avoids blocking a thread-pool thread on a network
            // round-trip, which matters on a health endpoint that orchestrators poll continuously.
            await _database.RunCommandAsync<BsonDocument>(
                new BsonDocument("ping", 1),
                cancellationToken: cts.Token);

            return HealthCheckResult.Healthy("MongoDB connection is active and responding to ping.");
        }
        catch (Exception ex)
        {
            // Catch all exceptions — including OperationCanceledException for timeout — and report as unhealthy.
            // The exception is carried rather than logged and dropped: "unhealthy" with no reason is
            // what makes an operator start guessing. A health check that throws is worse than one that
            // reports unhealthy, because one broken dependency would report every dependency as unknown,
            // which is the opposite of what a health endpoint is for.
            return new HealthCheckResult(
                context.Registration.FailureStatus,
                "MongoDB health check failed.",
                ex);
        }
    }
}
