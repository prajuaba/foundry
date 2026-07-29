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
        => RealTimeAccessPolicy.ToSubscriptionName(entityTypeName);

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

    /// <summary>
    /// Refuses a subscription the caller's roles do not permit.
    /// </summary>
    /// <remarks>
    /// The decision itself is <see cref="RealTimeAccessPolicy"/>, shared with SSE and WebSocket
    /// delivery. It used to live here alone, which is exactly why the other two transports did not
    /// apply it: the rule was a property of this class rather than of the framework.
    /// </remarks>
    private void ValidateSubscriptionRights(string entityName)
    {
        if (RealTimeAccessPolicy.MayObserve(Context.User, entityName, out var reason)) return;

        _logger.LogInformation(
            "Refused a real-time subscription for {ConnectionId} to {Entity}: {Reason}",
            Context.ConnectionId, entityName, reason);

        throw new HubException($"Unauthorized to subscribe to real-time events for {entityName}.");
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
