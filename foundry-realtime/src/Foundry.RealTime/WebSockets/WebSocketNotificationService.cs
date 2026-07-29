using System.Threading;
using System.Threading.Tasks;
using Foundry.Core.Audit;

namespace Foundry.RealTime.WebSockets;

/// <summary>
/// Real-time communications service broadcasting database mutations via raw WebSockets.
/// </summary>
public class WebSocketNotificationService : INotificationService
{
    private readonly WebSocketConnectionManager _connectionManager;

    public WebSocketNotificationService(WebSocketConnectionManager connectionManager)
    {
        _connectionManager = connectionManager;
    }

    public string ChannelName => "WebSockets";

    public async Task SendMutationAsync(AuditLogEntry entry, CancellationToken ct = default)
    {
        // Wrap the audit log entry in a WebSocket event message format
        var wsMessage = new
        {
            Type = "MutationEvent",
            Payload = entry
        };

        // Named, so delivery can be filtered by the entity's realTimeRoles rather than going to
        // every connected socket regardless of who is on the other end.
        await _connectionManager.BroadcastMessageAsync(wsMessage, entry.EntityType, ct);
    }
}
