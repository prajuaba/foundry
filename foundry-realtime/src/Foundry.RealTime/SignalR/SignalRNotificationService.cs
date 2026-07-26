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
        // Delivery goes only to subscription groups.
        //
        // There used to be an additional unconditional `Clients.All` send, which handed every
        // mutation to every connected client. NotificationHub validates [RealTime(roles: ...)] on
        // subscribe, and that firehose bypassed the check entirely -- and, in a multi-tenant
        // deployment, delivered one tenant's mutations to another's clients. An AuditLogEntry
        // carries PropertyDiffs, so what leaked was the changed values, not just metadata. Every
        // send reported success, so nothing surfaced it.
        var entityName = NotificationHub.ToSubscriptionName(entry.EntityType);

        // Must match the group NotificationHub.SubscribeToEntity joins. Delivery used
        // entry.EntityType, which is the assembly-qualified name, while subscribers join under the
        // simple name the client asked for -- so the two never matched and entity subscriptions
        // received nothing. The Clients.All send above masked it.
        await _hubContext.Clients
            .Group($"entity:{entityName}")
            .SendAsync("OnEntityMutationReceived", entry, cancellationToken: ct);

        if (!string.IsNullOrWhiteSpace(entry.EntityId))
        {
            await _hubContext.Clients
                .Group(NotificationHub.RecordGroupName(entityName, entry.EntityId))
                .SendAsync("OnRecordMutationReceived", entry, cancellationToken: ct);
        }
    }
}
