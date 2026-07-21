using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Foundry.Kafka.Consumer;

/// <summary>
/// Defines the interface for handling Kafka messages.
/// </summary>
public interface IKafkaMessageHandler
{
    /// <summary>
    /// Handles a Kafka message asynchronously.
    /// </summary>
    /// <param name="topic">The topic the message came from.</param>
    /// <param name="key">The key of the message.</param>
    /// <param name="value">The value of the message.</param>
    /// <param name="headers">The headers associated with the message.</param>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    Task HandleAsync(string topic, string key, string value, IDictionary<string, string> headers, CancellationToken ct);
}
