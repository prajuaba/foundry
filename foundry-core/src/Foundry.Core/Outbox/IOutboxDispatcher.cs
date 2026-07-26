using System.Threading;
using System.Threading.Tasks;

namespace Foundry.Core.Outbox;

/// <summary>
/// Defines the contract for dispatching an outbox message to its final destination (e.g., Kafka, MediatR, or SignalR).
/// </summary>
public interface IOutboxDispatcher
{
    /// <summary>
    /// Dispatches a serialized event payload to its destination.
    /// </summary>
    /// <param name="eventType">The assembly-qualified type name of the event.</param>
    /// <param name="payload">The serialized JSON payload of the event.</param>
    /// <param name="correlationId">Optional trace correlation ID.</param>
    /// <param name="traceParent">Optional W3C traceparent header.</param>
    /// <param name="ct">Cancellation token.</param>
    Task DispatchAsync(string eventType, string payload, string? correlationId = null, string? traceParent = null, CancellationToken ct = default);
}
