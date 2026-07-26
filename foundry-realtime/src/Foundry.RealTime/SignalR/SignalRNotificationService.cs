using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Foundry.Core.Audit;

namespace Foundry.RealTime.SignalR;

/// <summary>
/// Broadcasts database mutation events utilizing ASP.NET Core SignalR.
/// </summary>
public class SignalRNotificationService : INotificationService
{
    private readonly IHubContext<NotificationHub> _hubContext;

    public SignalRNotificationService(IHubContext<NotificationHub> hubContext)
    {
        _hubContext = hubContext;
    }

    public string ChannelName => "SignalR";

    public async Task SendMutationAsync(AuditLogEntry entry, CancellationToken ct = default)
    {
        // 1. Broadcast globally to all listeners
        await _hubContext.Clients.All.SendAsync("OnMutationReceived", entry, cancellationToken: ct);

        // 2. Broadcast to specific entity subscription group (e.g. entity:Invoice)
        string entityGroup = $"entity:{entry.EntityType}";
        await _hubContext.Clients.Group(entityGroup).SendAsync("OnEntityMutationReceived", entry, cancellationToken: ct);

        // 3. Broadcast to specific record subscription group (e.g. record:64b1f48e...)
        if (!string.IsNullOrWhiteSpace(entry.EntityId))
        {
            string recordGroup = $"record:{entry.EntityId}";
            await _hubContext.Clients.Group(recordGroup).SendAsync("OnRecordMutationReceived", entry, cancellationToken: ct);
        }
    }
}
