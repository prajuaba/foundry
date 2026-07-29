using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Foundry.Core.Audit;

namespace Foundry.RealTime.SSE;

/// <summary>
/// Broadcasts database mutation events using Server-Sent Events (SSE).
/// </summary>
public class SseNotificationService : INotificationService
{
    private readonly ConcurrentDictionary<string, SseClient> _clients = new();
    private readonly ILogger<SseNotificationService> _logger;

    public SseNotificationService(ILogger<SseNotificationService> logger)
    {
        _logger = logger;
    }

    public string ChannelName => "SSE";

    /// <summary>
    /// Registers a newly established SSE client connection.
    /// </summary>
    public SseClient RegisterClient(HttpResponse response, ClaimsPrincipal? user = null)
    {
        string id = Guid.NewGuid().ToString("N");
        
        // Setup SSE response headers
        response.ContentType = "text/event-stream";
        response.Headers["Cache-Control"] = "no-cache";
        response.Headers["Connection"] = "keep-alive";
        
        var client = new SseClient(id, response, user);
        _clients.TryAdd(id, client);
        _logger.LogDebug("SSE client registered: {Id}", id);
        
        return client;
    }

    /// <summary>
    /// Unregisters and cleans up an SSE client.
    /// </summary>
    public void UnregisterClient(string id)
    {
        if (_clients.TryRemove(id, out _))
        {
            _logger.LogDebug("SSE client disconnected: {Id}", id);
        }
    }

    /// <summary>
    /// Sends a mutation to the clients whose caller is allowed to see that entity.
    /// </summary>
    /// <remarks>
    /// This used to send to every connected client unconditionally. SignalR had the same shape and
    /// it was removed there as a leak — an <c>AuditLogEntry</c> carries the changed values — but the
    /// fix was applied to the transport rather than to the framework, so this one and the WebSocket
    /// one kept doing it. <see cref="RealTimeAccessPolicy"/> is now the one place that decides.
    /// </remarks>
    public async Task SendMutationAsync(AuditLogEntry entry, CancellationToken ct = default)
    {
        var tasks = new List<Task>();
        foreach (var (id, client) in _clients)
        {
            if (!RealTimeAccessPolicy.MayObserve(client.User, entry.EntityType, out var reason))
            {
                if (RealTimeAccessPolicy.IsKnownEntity(entry.EntityType))
                {
                    _logger.LogDebug(
                        "Withheld {Entity} mutation from SSE client {Id}: {Reason}",
                        entry.EntityType, id, reason);
                }
                else
                {
                    // Not a denial -- a misconfiguration. Nothing will ever be delivered for this
                    // entity on any transport, and silence is the only symptom.
                    _logger.LogWarning(
                        "No real-time delivery for {Entity}: {Reason}", entry.EntityType, reason);
                }
                continue;
            }

            tasks.Add(SendToClientWithFallback(id, client, entry, ct));
        }

        await Task.WhenAll(tasks);
    }

    private async Task SendToClientWithFallback(string id, SseClient client, AuditLogEntry entry, CancellationToken ct)
    {
        try
        {
            await client.SendEventAsync("mutation", entry, ct);
        }
        catch (Exception)
        {
            // If writing fails, the client probably closed the connection. Clean it up.
            UnregisterClient(id);
        }
    }
}
