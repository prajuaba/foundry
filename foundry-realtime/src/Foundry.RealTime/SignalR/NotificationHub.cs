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

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Type?> _hubTypeCache = new();

    /// <summary>
    /// Reduces an entity type name to the short form used in subscription group names.
    /// </summary>
    /// <remarks>
    /// Subscribers join under the name they asked for (<c>Invoice</c>), while an audit entry carries
    /// the assembly-qualified name (<c>MyApp.Domain.Invoice</c>). Both sides normalise through this
    /// method so the group a client joins is the group delivery targets; previously they did not
    /// match and entity subscriptions silently received nothing.
    /// </remarks>
    public static string ToSubscriptionName(string entityTypeName)
    {
        if (string.IsNullOrWhiteSpace(entityTypeName)) return string.Empty;

        var name = entityTypeName.Split(',')[0].Trim();
        var lastDot = name.LastIndexOf('.');
        if (lastDot >= 0 && lastDot < name.Length - 1) name = name.Substring(lastDot + 1);

        // Nested types render as Outer+Inner.
        var plus = name.LastIndexOf('+');
        if (plus >= 0 && plus < name.Length - 1) name = name.Substring(plus + 1);

        return name;
    }

    /// <summary>
    /// Builds the group name for a single record's subscription.
    /// </summary>
    /// <remarks>
    /// The entity is part of the key. It used to be <c>record:{id}</c> alone, while authorisation was
    /// checked against a client-supplied entity name — so a caller could pass an entity they are
    /// allowed to see, then join the record group of an id belonging to a restricted entity and
    /// receive its mutations. The check and the group key have to be derived from the same value.
    /// </remarks>
    public static string RecordGroupName(string entityName, string recordId)
        => $"record:{ToSubscriptionName(entityName)}:{recordId.Trim()}";

    private void ValidateSubscriptionRights(string entityName)
    {
        var type = _hubTypeCache.GetOrAdd(ToSubscriptionName(entityName), name =>
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException)
                {
                    // One unloadable assembly must not make every subscribe attempt fail.
                    continue;
                }

                var match = types.FirstOrDefault(t =>
                    t.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                    || t.FullName?.Equals(name, StringComparison.OrdinalIgnoreCase) == true);

                if (match != null) return match;
            }

            return null;
        });

        if (type == null)
        {
            // Fail closed. An unresolvable name previously came back with no attribute and was
            // therefore treated as authorised, so a caller naming a type this process cannot see was
            // granted a subscription whose access rules were unknown.
            throw new HubException($"Unknown entity '{entityName}'; cannot authorise a subscription to it.");
        }

        var rtAttr = type.GetCustomAttribute<Foundry.Core.Attributes.RealTimeAttribute>();

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

        string groupName = $"entity:{ToSubscriptionName(entityName)}";
        await Groups.AddToGroupAsync(Context.ConnectionId, groupName);
        _logger.LogInformation("Client {ConnectionId} subscribed to entity group: {GroupName}", Context.ConnectionId, groupName);
    }

    /// <summary>
    /// Allows a client to unsubscribe from real-time events for a specific entity type.
    /// </summary>
    public async Task UnsubscribeFromEntity(string entityName)
    {
        if (string.IsNullOrWhiteSpace(entityName)) return;
        
        string groupName = $"entity:{ToSubscriptionName(entityName)}";
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

        string groupName = RecordGroupName(entityName, recordId);
        await Groups.AddToGroupAsync(Context.ConnectionId, groupName);
        _logger.LogInformation("Client {ConnectionId} subscribed to record group: {GroupName}", Context.ConnectionId, groupName);
    }

    /// <summary>
    /// Allows a client to unsubscribe from real-time updates for a single specific record ID.
    /// </summary>
    /// <remarks>
    /// Takes the entity name as well as the record id, mirroring <see cref="SubscribeToRecord"/>.
    /// This is a breaking change for clients: record groups are now keyed by entity and id together,
    /// so an id-only unsubscribe would silently remove the connection from a group it never joined
    /// and leave the real subscription in place.
    /// </remarks>
    public async Task UnsubscribeFromRecord(string entityName, string recordId)
    {
        if (string.IsNullOrWhiteSpace(entityName) || string.IsNullOrWhiteSpace(recordId)) return;

        string groupName = RecordGroupName(entityName, recordId);
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
