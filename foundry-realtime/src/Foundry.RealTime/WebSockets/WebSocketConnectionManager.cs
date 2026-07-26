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
    public async Task BroadcastMessageAsync(object message, CancellationToken ct = default)
    {
        var tasks = new List<Task>();
        foreach (var (id, socket) in _sockets)
        {
            if (socket.State == WebSocketState.Open)
            {
                tasks.Add(SendMessageAsync(socket, message, ct));
            }
            else
            {
                tasks.Add(RemoveSocketAsync(id, "Socket state no longer open"));
            }
        }
        await Task.WhenAll(tasks);
    }
}
