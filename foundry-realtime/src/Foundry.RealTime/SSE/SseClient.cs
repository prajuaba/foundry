using System;
using System.IO;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace Foundry.RealTime.SSE;

/// <summary>
/// Represents a single active SSE (Server-Sent Events) subscription stream.
/// </summary>
public class SseClient
{
    private readonly HttpResponse _response;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public SseClient(string id, HttpResponse response, ClaimsPrincipal? user = null)
    {
        Id = id;
        _response = response;
        User = user;
    }

    public string Id { get; }

    /// <summary>
    /// The caller this stream belongs to, captured when the connection was accepted.
    /// </summary>
    /// <remarks>
    /// A stream had no idea who was reading it, so there was nothing to check an entity's
    /// <c>realTimeRoles</c> against and every client received every mutation. The principal is
    /// captured once at connection rather than read per event: the request is long gone by the time
    /// a mutation arrives, and <c>HttpContext.User</c> with it.
    /// </remarks>
    public ClaimsPrincipal? User { get; }

    /// <summary>
    /// Writes an SSE formatted message event block to the connection stream.
    /// </summary>
    public async Task SendEventAsync(string eventName, object data, CancellationToken ct = default)
    {
        string json = JsonSerializer.Serialize(data, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        // Format according to the SSE standard:
        // event: [name]\n
        // data: [json]\n\n
        string payload = $"event: {eventName}\ndata: {json}\n\n";

        await _writeLock.WaitAsync(ct);
        try
        {
            await _response.WriteAsync(payload, ct);
            await _response.Body.FlushAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
