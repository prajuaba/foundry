using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Foundry.Connectors;

public class FoundryConnectorHealthCheck : IHealthCheck
{
    private readonly IFoundryConnector _connector;

    public FoundryConnectorHealthCheck(IFoundryConnector connector)
    {
        _connector = connector ?? throw new ArgumentNullException(nameof(connector));
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var isHealthy = await _connector.CheckHealthAsync(cancellationToken);

        if (isHealthy)
        {
            return HealthCheckResult.Healthy($"External Connector '{_connector.Name}' ({_connector.Type}) is healthy and responsive.");
        }

        return HealthCheckResult.Unhealthy($"External Connector '{_connector.Name}' ({_connector.Type}) failed health check.");
    }
}
