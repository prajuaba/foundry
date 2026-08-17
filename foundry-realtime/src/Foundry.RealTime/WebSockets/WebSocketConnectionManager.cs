using System;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Claims;
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
    /// <summary>
    /// A live socket and the caller it belongs to.
    /// </summary>
    /// <remarks>
    /// The principal is captured when the socket is accepted. Without it there was nothing to check
    /// an entity's <c>realTimeRoles</c> against, so every socket received every mutation in the
    /// system — the firehose that was removed from SignalR for leaking the changed values, left in
    /// place here because the fix was made to that transport rather than to the rule.
    /// </remarks>
    public sealed record Connection(WebSocket Socket, ClaimsPrincipal? User);

    private readonly ConcurrentDictionary<string, Connection> _sockets = new();
    private readonly ILogger<WebSocketConnectionManager> _logger;

    public WebSocketConnectionManager(ILogger<WebSocketConnectionManager> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Registers a newly accepted WebSocket connection.
    /// </summary>
    public string AddSocket(WebSocket socket, ClaimsPrincipal? user = null)
    {
        string id = Guid.NewGuid().ToString("N");
        _sockets.TryAdd(id, new Connection(socket, user));
        _logger.LogDebug("WebSocket registered: {Id}", id);
        return id;
    }

    /// <summary>
    /// Gets all active connections.
    /// </summary>
    public ConcurrentDictionary<string, Connection> GetAllSockets() => _sockets;

    /// <summary>
    /// Safely removes and closes a WebSocket connection.
    /// </summary>
    public async Task RemoveSocketAsync(string id, string reason)
    {
        if (_sockets.TryRemove(id, out var connection))
        {
            var socket = connection.Socket;
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
        => await BroadcastMessageAsync(message, entityTypeName: null, ct);

    /// <summary>
    /// Broadcasts to the sockets whose caller may observe <paramref name="entityTypeName"/>.
    /// </summary>
    /// <remarks>
    /// A null entity means the message is not about one — a handshake frame — and goes to everyone.
    /// Anything carrying entity data must name it, so <see cref="RealTimeAccessPolicy"/> can decide.
    /// </remarks>
    public async Task BroadcastMessageAsync(object message, string? entityTypeName, CancellationToken ct = default)
        => await BroadcastMessageAsync(message, entityTypeName, eventTenantId: null, ct);

    /// <summary>
    /// Broadcasts to the sockets whose caller may observe <paramref name="entityTypeName"/> and
    /// belongs to <paramref name="eventTenantId"/>.
    /// </summary>
    public async Task BroadcastMessageAsync(
        object message, string? entityTypeName, string? eventTenantId, CancellationToken ct = default)
    {
        var tasks = new List<Task>();
        foreach (var (id, connection) in _sockets)
        {
            if (connection.Socket.State != WebSocketState.Open)
            {
                tasks.Add(RemoveSocketAsync(id, "Socket state no longer open"));
                continue;
            }

            if (entityTypeName is not null
                && !RealTimeAccessPolicy.MayObserve(connection.User, entityTypeName, eventTenantId, out var reason))
            {
                if (RealTimeAccessPolicy.IsKnownEntity(entityTypeName))
                {
                    _logger.LogDebug(
                        "Withheld {Entity} mutation from WebSocket {Id}: {Reason}", entityTypeName, id, reason);
                }
                else
                {
                    _logger.LogWarning(
                        "No real-time delivery for {Entity}: {Reason}", entityTypeName, reason);
                }
                continue;
            }

            tasks.Add(SendWithCleanupAsync(id, connection.Socket, message, ct));
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
