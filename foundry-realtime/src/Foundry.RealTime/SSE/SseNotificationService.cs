using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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
    public SseClient RegisterClient(HttpResponse response)
    {
        string id = Guid.NewGuid().ToString("N");
        
        // Setup SSE response headers
        response.ContentType = "text/event-stream";
        response.Headers["Cache-Control"] = "no-cache";
        response.Headers["Connection"] = "keep-alive";
        
        var client = new SseClient(id, response);
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

    public async Task SendMutationAsync(AuditLogEntry entry, CancellationToken ct = default)
    {
        var tasks = new List<Task>();
        foreach (var (id, client) in _clients)
        {
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
