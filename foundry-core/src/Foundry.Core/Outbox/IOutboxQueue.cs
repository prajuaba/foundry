using System.Threading;
using System.Threading.Tasks;

namespace Foundry.Core.Outbox;

/// <summary>
/// Defines the contract for queueing messages or events to be processed transactionally via the Outbox pattern.
/// </summary>
public interface IOutboxQueue
{
    /// <summary>
    /// Enqueues an event payload into the database outbox.
    /// </summary>
    /// <typeparam name="TEvent">Type of the event object.</typeparam>
    /// <param name="eventData">The event payload to enqueue.</param>
    /// <param name="ct">Cancellation token.</param>
    Task EnqueueAsync<TEvent>(TEvent eventData, CancellationToken ct) where TEvent : class;
}
