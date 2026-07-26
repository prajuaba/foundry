using System.Threading;
using System.Threading.Tasks;
using Foundry.Core.Audit;

namespace Foundry.RealTime;

/// <summary>
/// Defines the broadcast capabilities for a specific real-time communications channel (e.g., SignalR, WebSockets, SSE).
/// </summary>
public interface INotificationService
{
    /// <summary>
    /// Gets the name of the real-time channel.
    /// </summary>
    string ChannelName { get; }

    /// <summary>
    /// Broadcasts a database mutation event to all active clients on this channel.
    /// </summary>
    Task SendMutationAsync(AuditLogEntry entry, CancellationToken ct = default);
}
