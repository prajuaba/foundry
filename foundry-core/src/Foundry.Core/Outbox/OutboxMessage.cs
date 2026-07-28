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

    /// <summary>Gets or sets the trace correlation ID (e.g. system transaction context).</summary>
    public string? CorrelationId { get; set; }

    /// <summary>Gets or sets the W3C traceparent context header for OpenTelemetry correlation.</summary>
    public string? TraceParent { get; set; }

    /// <summary>Gets or sets any error message recorded during the last failure.</summary>
    public string? ErrorMessage { get; set; }
}
