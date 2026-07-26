using System.Threading;
using System.Threading.Tasks;

namespace Foundry.Connectors;

public interface IFoundryConnector
{
    string Name { get; }
    ConnectorType Type { get; }
    Task<TResponse?> ExecuteAsync<TRequest, TResponse>(string endpoint, TRequest payload, CancellationToken cancellationToken = default);
    Task<bool> CheckHealthAsync(CancellationToken cancellationToken = default);
}
