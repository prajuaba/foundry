using System;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Foundry.RealTime.WebSockets;

/// <summary>
/// Thread-safe manager for active raw WebSocket connections.
/// </summary>
public class WebSocketConnectionManager
{
    private readonly ConcurrentDictionary<string, WebSocket> _sockets = new();
    private readonly ILogger<WebSocketConnectionManager> _logger;

    public WebSocketConnectionManager(ILogger<WebSocketConnectionManager> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Registers a newly accepted WebSocket connection.
    /// </summary>
    public string AddSocket(WebSocket socket)
    {
        string id = Guid.NewGuid().ToString("N");
        _sockets.TryAdd(id, socket);
        _logger.LogDebug("WebSocket registered: {Id}", id);
        return id;
    }

    /// <summary>
    /// Gets all active connections.
    /// </summary>
    public ConcurrentDictionary<string, WebSocket> GetAllSockets() => _sockets;

    /// <summary>
    /// Safely removes and closes a WebSocket connection.
    /// </summary>
    public async Task RemoveSocketAsync(string id, string reason)
    {
        if (_sockets.TryRemove(id, out var socket))
        {
            if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)
            {
                try
                {
                    await socket.CloseAsync(
                        closeStatus: WebSocketCloseStatus.NormalClosure,
                        statusDescription: reason,
                        cancellationToken: CancellationToken.None
                    );
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error closing WebSocket socket {Id}", id);
                }
            }
            socket.Dispose();
            _logger.LogDebug("WebSocket socket {Id} closed and removed: {Reason}", id, reason);
        }
    }

    /// <summary>
    /// Sends a JSON object to a specific socket.
    /// </summary>
    public async Task SendMessageAsync(WebSocket socket, object message, CancellationToken ct = default)
    {
        if (socket.State != WebSocketState.Open) return;

        string json = JsonSerializer.Serialize(message, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        byte[] bytes = Encoding.UTF8.GetBytes(json);

        await socket.SendAsync(
            buffer: new ArraySegment<byte>(bytes),
            messageType: WebSocketMessageType.Text,
            endOfMessage: true,
            cancellationToken: ct
        );
    }

    /// <summary>
    /// Broadcasts a message to all active sockets.
    /// </summary>
    /// <remarks>
    /// Each socket is isolated. A connection can report <c>Open</c> and still throw on write — the
    /// peer may vanish between the state check and the send — and an unisolated failure propagated
    /// out of <c>Task.WhenAll</c>, so one dead client suppressed the broadcast for every other client
    /// on this transport. The SSE channel already isolated per client; this one did not.
    /// </remarks>
    public async Task BroadcastMessageAsync(object message, CancellationToken ct = default)
    {
        var tasks = new List<Task>();
        foreach (var (id, socket) in _sockets)
        {
            if (socket.State == WebSocketState.Open)
            {
                tasks.Add(SendWithCleanupAsync(id, socket, message, ct));
            }
            else
            {
                tasks.Add(RemoveSocketAsync(id, "Socket state no longer open"));
            }
        }
        await Task.WhenAll(tasks);
    }

    private async Task SendWithCleanupAsync(string id, WebSocket socket, object message, CancellationToken ct)
    {
        try
        {
            await SendMessageAsync(socket, message, ct);
        }
        catch (Exception ex)
        {
            // The write failed, so this connection is gone. Drop it rather than retrying it on every
            // subsequent mutation.
            _logger.LogDebug(ex, "Dropping WebSocket {Id} after a failed send.", id);
            await RemoveSocketAsync(id, "Send failed");
        }
    }
}
