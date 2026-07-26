using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Foundry.Cli.Lsp;

/// <summary>
/// Lightweight JSON-RPC Language Server Protocol (LSP) server for VS Code extension integration.
/// Supports inline schema diagnostics, completions, and hover documentation.
/// </summary>
public static class LspServer
{
    public static async Task<int> RunAsync()
    {
        Console.Error.WriteLine("[Foundry LSP] Language Server listening on stdio...");

        // The LSP base protocol frames messages by *byte* count, so the transport has to work
        // in bytes throughout. Reading the body with a StreamReader into a char[] sized by
        // Content-Length desynchronises the stream as soon as the payload contains any
        // non-ASCII character — a single 'é' is two bytes but one char, leaving a trailing byte
        // that corrupts every subsequent frame.
        await using var input = Console.OpenStandardInput();
        await using var output = Console.OpenStandardOutput();

        while (true)
        {
            var contentLength = await ReadHeadersAsync(input);
            if (contentLength < 0) break;      // clean EOF
            if (contentLength == 0) continue;  // empty body, nothing to dispatch

            var body = await ReadExactlyAsync(input, contentLength);
            if (body == null) break;           // truncated stream

            var payload = Encoding.UTF8.GetString(body);

            try
            {
                var response = ProcessLspMessage(payload);
                if (response != null)
                    await WriteMessageAsync(output, response);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[LSP Error] {ex.Message}");
            }
        }

        return 0;
    }

    /// <summary>
    /// Reads the header block and returns the declared Content-Length in bytes,
    /// or -1 at end of stream.
    /// </summary>
    private static async Task<int> ReadHeadersAsync(Stream input)
    {
        var contentLength = -1;
        var sawAnyHeader = false;

        while (true)
        {
            var line = await ReadHeaderLineAsync(input);
            if (line == null) return sawAnyHeader ? 0 : -1;

            if (line.Length == 0)
                return contentLength < 0 ? 0 : contentLength;

            sawAnyHeader = true;

            const string prefix = "Content-Length:";
            if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(line.Substring(prefix.Length).Trim(), out var parsed)
                && parsed >= 0)
            {
                contentLength = parsed;
            }
        }
    }

    /// <summary>
    /// Reads a single CRLF-terminated header line as ASCII, returning null at end of stream.
    /// </summary>
    private static async Task<string?> ReadHeaderLineAsync(Stream input)
    {
        var buffer = new MemoryStream();
        var one = new byte[1];

        while (true)
        {
            var read = await input.ReadAsync(one.AsMemory(0, 1));
            if (read == 0)
                return buffer.Length == 0 ? null : Encoding.ASCII.GetString(buffer.ToArray()).TrimEnd('\r');

            if (one[0] == (byte)'\n')
                return Encoding.ASCII.GetString(buffer.ToArray()).TrimEnd('\r');

            buffer.WriteByte(one[0]);
        }
    }

    /// <summary>
    /// Reads exactly <paramref name="count"/> bytes, returning null if the stream ends early.
    /// </summary>
    private static async Task<byte[]?> ReadExactlyAsync(Stream input, int count)
    {
        var buffer = new byte[count];
        var offset = 0;

        while (offset < count)
        {
            var read = await input.ReadAsync(buffer.AsMemory(offset, count - offset));
            if (read == 0) return null;
            offset += read;
        }

        return buffer;
    }

    /// <summary>
    /// Writes a JSON-RPC message with a byte-accurate Content-Length header and no trailing
    /// newline, which would otherwise be counted against the next frame.
    /// </summary>
    private static async Task WriteMessageAsync(Stream output, object response)
    {
        var json = JsonSerializer.Serialize(response);
        var bodyBytes = Encoding.UTF8.GetBytes(json);
        var headerBytes = Encoding.ASCII.GetBytes($"Content-Length: {bodyBytes.Length}\r\n\r\n");

        await output.WriteAsync(headerBytes);
        await output.WriteAsync(bodyBytes);
        await output.FlushAsync();
    }

    private static object? ProcessLspMessage(string jsonMessage)
    {
        using var doc = JsonDocument.Parse(jsonMessage);
        var root = doc.RootElement;

        if (!root.TryGetProperty("method", out var methodProp)) return null;
        var method = methodProp.GetString();

        if (!root.TryGetProperty("id", out var idProp)) return null;
        var id = idProp.GetInt64();

        if (method == "initialize")
        {
            return new
            {
                jsonrpc = "2.0",
                id = id,
                result = new
                {
                    capabilities = new
                    {
                        textDocumentSync = 1,
                        completionProvider = new { resolveProvider = true, triggerCharacters = new[] { "\"", ":" } },
                        hoverProvider = true
                    }
                }
            };
        }
        else if (method == "textDocument/completion")
        {
            return new
            {
                jsonrpc = "2.0",
                id = id,
                result = new[]
                {
                    new { label = "UniqueIndex", kind = 14, detail = "Ensures database index uniqueness" },
                    new { label = "Encrypt", kind = 14, detail = "AES-256 Envelope Encryption" },
                    new { label = "TenantKey", kind = 14, detail = "Multi-Tenant Partition Key" },
                    new { label = "PiiEmail", kind = 14, detail = "PII Data Masking for Email" },
                    new { label = "PiiCreditCard", kind = 14, detail = "PII Data Masking for Credit Cards" }
                }
            };
        }

        return new { jsonrpc = "2.0", id = id, result = (object?)null };
    }
}
