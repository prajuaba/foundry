using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace Foundry.RealTime.SignalR;

/// <summary>
/// SignalR Hub for real-time client connections.
/// </summary>
public class NotificationHub : Hub
{
    private readonly ILogger<NotificationHub> _logger;

    public NotificationHub(ILogger<NotificationHub> logger)
    {
        _logger = logger;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Foundry.Core.Attributes.RealTimeAttribute?> _hubAttributeCache = new();

    private void ValidateSubscriptionRights(string entityName)
    {
        var rtAttr = _hubAttributeCache.GetOrAdd(entityName, name =>
        {
            Type? type = null;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                type = assembly.GetTypes().FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase) 
                    || t.FullName?.Equals(name, StringComparison.OrdinalIgnoreCase) == true);
                if (type != null) break;
            }
            return type?.GetCustomAttribute<Foundry.Core.Attributes.RealTimeAttribute>();
        });

        if (rtAttr != null && rtAttr.Roles.Length > 0)
        {
            bool isAuthorized = false;
            foreach (var role in rtAttr.Roles)
            {
                if (Context.User?.IsInRole(role) == true)
                {
                    isAuthorized = true;
                    break;
                }
            }
            if (!isAuthorized)
            {
                throw new HubException($"Unauthorized to subscribe to real-time events for {entityName}.");
            }
        }
    }

    /// <summary>
    /// Allows a client to subscribe to real-time events for a specific entity type (e.g., "Customer", "Invoice").
    /// </summary>
    public async Task SubscribeToEntity(string entityName)
    {
        if (string.IsNullOrWhiteSpace(entityName)) return;
        
        ValidateSubscriptionRights(entityName);
        
        string groupName = $"entity:{entityName.Trim()}";
        await Groups.AddToGroupAsync(Context.ConnectionId, groupName);
        _logger.LogInformation("Client {ConnectionId} subscribed to entity group: {GroupName}", Context.ConnectionId, groupName);
    }

    /// <summary>
    /// Allows a client to unsubscribe from real-time events for a specific entity type.
    /// </summary>
    public async Task UnsubscribeFromEntity(string entityName)
    {
        if (string.IsNullOrWhiteSpace(entityName)) return;
        
        string groupName = $"entity:{entityName.Trim()}";
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, groupName);
        _logger.LogInformation("Client {ConnectionId} unsubscribed from entity group: {GroupName}", Context.ConnectionId, groupName);
    }

    /// <summary>
    /// Allows a client to subscribe to real-time updates for a single specific record ID.
    /// </summary>
    public async Task SubscribeToRecord(string entityName, string recordId)
    {
        if (string.IsNullOrWhiteSpace(entityName) || string.IsNullOrWhiteSpace(recordId)) return;
        
        ValidateSubscriptionRights(entityName);
        
        string groupName = $"record:{recordId.Trim()}";
        await Groups.AddToGroupAsync(Context.ConnectionId, groupName);
        _logger.LogInformation("Client {ConnectionId} subscribed to record group: {GroupName}", Context.ConnectionId, groupName);
    }

    /// <summary>
    /// Allows a client to unsubscribe from real-time updates for a single specific record ID.
    /// </summary>
    public async Task UnsubscribeFromRecord(string recordId)
    {
        if (string.IsNullOrWhiteSpace(recordId)) return;
        
        string groupName = $"record:{recordId.Trim()}";
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, groupName);
        _logger.LogInformation("Client {ConnectionId} unsubscribed from record group: {GroupName}", Context.ConnectionId, groupName);
    }

    public override Task OnConnectedAsync()
    {
        _logger.LogDebug("Client connected to RealTimeHub: {ConnectionId}", Context.ConnectionId);
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogDebug(exception, "Client disconnected from RealTimeHub: {ConnectionId}", Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}
