using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Foundry.Kafka.Configuration;

namespace Foundry.Kafka.Diagnostics;

/// <summary>
/// Monitors connection health of the Kafka cluster by querying metadata via an AdminClient.
/// Suitable for containerized cloud deployment health checks (Kubernetes liveness/readiness probes).
/// </summary>
public sealed class KafkaHealthCheck : IHealthCheck
{
    private readonly KafkaOptions _options;
    private readonly Func<AdminClientConfig, IAdminClient> _adminClientFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="KafkaHealthCheck"/> class.
    /// </summary>
    /// <param name="options">The Kafka options config.</param>
    /// <param name="adminClientFactory">An optional custom factory to create the underlying AdminClient (useful for unit testing).</param>
    public KafkaHealthCheck(IOptions<KafkaOptions> options, Func<AdminClientConfig, IAdminClient>? adminClientFactory = null)
    {
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _adminClientFactory = adminClientFactory ?? (config => new AdminClientBuilder(config).Build());
    }

    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var config = new AdminClientConfig
            {
                BootstrapServers = _options.BootstrapServers,
                ClientId = $"{_options.ClientId}-healthcheck"
            };

            using var adminClient = _adminClientFactory(config);

            // Query Kafka cluster metadata to verify connectivity (timeout after 3 seconds)
            adminClient.GetMetadata(TimeSpan.FromSeconds(3));

            return Task.FromResult(HealthCheckResult.Healthy("Kafka cluster is reachable and responding to metadata requests."));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new HealthCheckResult(context.Registration.FailureStatus, "Kafka cluster health check query failed.", ex));
        }
    }
}
