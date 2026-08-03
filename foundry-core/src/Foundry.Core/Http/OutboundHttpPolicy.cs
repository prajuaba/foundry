using System;
using System.Net.Http;

namespace Foundry.Core.Http;

/// <summary>
/// What Foundry allows an outbound HTTP call to do, wherever it is made from.
/// </summary>
/// <remarks>
/// <para>
/// This framework calls out in two places: a workflow's <c>ExternalApi</c> action, and the
/// connectors. Both talk to systems Foundry does not control, and both were hardened separately —
/// which is to say the workflow engine was hardened and the connectors were not, because fixing one
/// outbound path did not prompt anyone to look at the other. That is the same shape as a rule with
/// several implementations, one cycle earlier: before the rule can drift it has to exist in one
/// place, and it did not.
/// </para>
/// <para>
/// It does now. Both callers configure their clients from here, so a limit added for one applies to
/// the other by construction rather than by someone remembering.
/// </para>
/// </remarks>
public static class OutboundHttpPolicy
{
    /// <summary>Largest response body Foundry will read from an external service.</summary>
    /// <remarks>
    /// A response is read whole into memory and, for a workflow action, written into an activity-log
    /// document with MongoDB's 16 MB ceiling. Exceeding this throws, which surfaces as a failed call
    /// rather than as an exhausted process.
    /// </remarks>
    public const long MaxResponseBytes = 1024 * 1024;

    /// <summary>How long a single outbound attempt may take.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The primary handler for any client calling a system Foundry does not control.
    /// </summary>
    /// <remarks>
    /// Redirects are not followed. The destination is configuration — a connector's base URL, a
    /// workflow action's URL — so a caller cannot choose it; but the service at that destination can
    /// answer 302 and choose the next one, including a link-local metadata endpoint or an address
    /// reachable only from inside the network. For a connector the request carries that connector's
    /// credentials, which makes the redirect a way to have them presented somewhere they were never
    /// configured to go. A 3xx now surfaces as a non-success status instead.
    /// </remarks>
    public static HttpClientHandler CreateHandler() => new() { AllowAutoRedirect = false };

    /// <summary>Applies the response and timeout limits to a client.</summary>
    public static void Configure(HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);

        client.MaxResponseContentBufferSize = MaxResponseBytes;
        client.Timeout = Timeout;
    }

    /// <summary>
    /// Rejects a value that cannot safely be sent as an HTTP header.
    /// </summary>
    /// <remarks>
    /// Rejected rather than sanitised: silently stripping a character sends a header the caller did
    /// not write, and for something like a SOAP action that changes which operation the remote service
    /// performs.
    /// </remarks>
    public static string RequireHeaderSafe(string? value, string what)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        if (value.Contains('\r') || value.Contains('\n') || value.Contains('"'))
        {
            throw new ArgumentException(
                $"{what} contains a character that cannot be sent in an HTTP header "
                + "(a line break or a quotation mark).", nameof(value));
        }

        return value;
    }
}
