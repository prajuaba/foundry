using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Foundry.Connectors;

/// <summary>
/// Shared response handling for the connectors.
/// </summary>
/// <remarks>
/// All three connectors called <c>EnsureSuccessStatusCode</c>, which throws carrying only the status
/// code. For components whose entire purpose is calling someone else's API, the remote body is the
/// one thing that makes a failure diagnosable, and discarding it turns every 4xx into guesswork.
/// Centralised so the three cannot drift apart on it.
/// </remarks>
internal static class ConnectorResponse
{
    private const int MaxBodyLength = 2000;

    /// <summary>
    /// Throws an <see cref="HttpRequestException"/> naming the connector, status and remote body when
    /// the response is not successful.
    /// </summary>
    internal static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        string connectorName,
        string endpoint,
        CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;

        var body = await ReadBodySafelyAsync(response, ct);

        throw new HttpRequestException(
            $"[{connectorName}] {(int)response.StatusCode} {response.ReasonPhrase} from "
            + $"{(string.IsNullOrEmpty(endpoint) ? response.RequestMessage?.RequestUri?.ToString() ?? "(base address)" : endpoint)}"
            + (string.IsNullOrWhiteSpace(body) ? "" : $": {body}"),
            inner: null,
            statusCode: response.StatusCode);
    }

    /// <summary>
    /// Reads a response body for inclusion in an error message, never throwing in the process.
    /// </summary>
    /// <remarks>
    /// Truncated, because some services answer a 500 with a full HTML page. A failure to read the
    /// body must not replace the real status-code error with a less useful one.
    /// </remarks>
    internal static async Task<string> ReadBodySafelyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = (await response.Content.ReadAsStringAsync(ct)).Trim();
            return body.Length <= MaxBodyLength ? body : body.Substring(0, MaxBodyLength) + "... (truncated)";
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }
}
