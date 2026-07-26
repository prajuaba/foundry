using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Foundry.Core.Audit;

namespace Foundry.RealTime;

/// <summary>
/// Default implementation of the real-time notification broker.
/// </summary>
public class RealTimeNotificationBroker : IRealTimeNotificationBroker
{
    private readonly IEnumerable<INotificationService> _channels;
    private readonly ILogger<RealTimeNotificationBroker> _logger;

    public RealTimeNotificationBroker(IEnumerable<INotificationService> channels, ILogger<RealTimeNotificationBroker> logger)
    {
        _channels = channels;
        _logger = logger;
    }

    public async Task BroadcastMutationAsync(AuditLogEntry entry, CancellationToken ct = default)
    {
        _logger.LogDebug("Broadcasting mutation {Id} for entity {Entity} ({Action})", entry.Id, entry.EntityType, entry.Action);
        
        var tasks = new List<Task>();
        foreach (var channel in _channels)
        {
            tasks.Add(SendToChannelAsync(channel, entry, ct));
        }

        await Task.WhenAll(tasks);
    }

    private async Task SendToChannelAsync(INotificationService channel, AuditLogEntry entry, CancellationToken ct)
    {
        try
        {
            await channel.SendMutationAsync(entry, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to broadcast mutation to real-time channel: {ChannelName}", channel.ChannelName);
        }
    }
}
