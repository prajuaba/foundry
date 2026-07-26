using System;
using System.IO;
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

    public SseClient(string id, HttpResponse response)
    {
        Id = id;
        _response = response;
    }

    public string Id { get; }

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
