using System.Threading;
using System.Threading.Tasks;
using Foundry.Core.Audit;

namespace Foundry.RealTime;

/// <summary>
/// Orchestrator responsible for routing data access layer mutations to all enabled communication channels.
/// </summary>
public interface IRealTimeNotificationBroker
{
    /// <summary>
    /// Broadcasts a mutation event across SignalR, WebSockets, and Server-Sent Events (SSE).
    /// </summary>
    Task BroadcastMutationAsync(AuditLogEntry entry, CancellationToken ct = default);
}
