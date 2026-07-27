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

    /// <inheritdoc />
    /// <remarks>
    /// The call is guarded. Each connector's own check catches its transport failures, but not
    /// everything reaches it as one — a malformed base URL throws from the <c>HttpClient</c>
    /// constructor path, and a cancelled request throws its own type. An exception escaping here
    /// propagates out of the health endpoint, so **one broken dependency reports every dependency as
    /// unknown**, which is the opposite of what a health endpoint is for.
    /// </remarks>
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var isHealthy = await _connector.CheckHealthAsync(cancellationToken);

            return isHealthy
                ? HealthCheckResult.Healthy(
                    $"External Connector '{_connector.Name}' ({_connector.Type}) is healthy and responsive.")
                : HealthCheckResult.Unhealthy(
                    $"External Connector '{_connector.Name}' ({_connector.Type}) failed health check.");
        }
        catch (Exception ex)
        {
            // The exception is carried rather than logged and dropped: "unhealthy" with no reason is
            // what makes an operator start guessing.
            return HealthCheckResult.Unhealthy(
                $"External Connector '{_connector.Name}' ({_connector.Type}) could not be checked: {ex.Message}",
                ex);
        }
    }
}
