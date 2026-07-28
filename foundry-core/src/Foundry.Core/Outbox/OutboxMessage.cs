using System;
using Foundry.Core.Entities;
using MongoDB.Bson;

namespace Foundry.Core.Outbox;

/// <summary>
/// Represents a message stored in the database outbox for transactional, asynchronous publishing.
/// </summary>
public record OutboxMessage : BaseEntity<ObjectId>
{
    /// <summary>Gets or sets the type name of the event or message.</summary>
    public string EventType { get; set; } = string.Empty;

    /// <summary>Gets or sets the serialized JSON payload of the event.</summary>
    public string Payload { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the destination topic, when the entity names one.
    /// </summary>
    /// <remarks>
    /// Null means "use the dispatcher's default naming", which is the case for every schema that does
    /// not declare <c>kafkaTopic</c>. The field exists because the message previously carried no
    /// destination at all: the dispatcher derived one from <see cref="EventType"/> and a declared
    /// topic had nowhere to travel, so it reached the generated consumer and never the publisher.
    /// </remarks>
    public string? Topic { get; set; }

    /// <summary>Gets or sets the date and time when the outbox message was created.</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Gets or sets the date and time when the message was successfully processed/published.</summary>
    public DateTime? ProcessedAt { get; set; }

    /// <summary>Gets or sets the number of publication attempts made.</summary>
    public int RetryCount { get; set; } = 0;

    /// <summary>
    /// Earliest time the next publication attempt may be made, or null to attempt immediately.
    /// </summary>
    /// <remarks>
    /// Retries used to happen on the polling interval with no delay between them. With a two-second
    /// poll and a ceiling of five attempts, **a broker outage of ten seconds exhausted every pending
    /// message** — which is the one thing an outbox exists to survive. Backing off spreads the same
    /// five attempts over minutes instead of seconds.
    /// </remarks>
    public DateTime? NextAttemptAt { get; set; }

    /// <summary>
    /// When the message was given up on, or null while it is still being attempted.
    /// </summary>
    /// <remarks>
    /// Exhaustion used to be implicit: the query selected <c>RetryCount &lt; 5</c>, so the fifth
    /// failure simply stopped matching and the message vanished from the worker's attention with
    /// nothing logged and nothing to distinguish it from one that had been published. An operator
    /// watching the queue drain saw exactly what success looks like.
    /// </remarks>
    public DateTime? DeadLetteredAt { get; set; }

    /// <summary>Gets or sets the trace correlation ID (e.g. system transaction context).</summary>
    public string? CorrelationId { get; set; }

    /// <summary>Gets or sets the W3C traceparent context header for OpenTelemetry correlation.</summary>
    public string? TraceParent { get; set; }

    /// <summary>Gets or sets any error message recorded during the last failure.</summary>
    public string? ErrorMessage { get; set; }
}
