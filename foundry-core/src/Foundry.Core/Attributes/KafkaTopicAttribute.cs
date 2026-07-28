using System;

namespace Foundry.Core.Attributes;

/// <summary>
/// Names the Kafka topic this entity's domain events are published to.
/// </summary>
/// <remarks>
/// <para>
/// A schema may declare <c>kafkaTopic</c>, and the compiler used that name for one thing only: the
/// topic the generated consumer subscribes to. Nothing carried it to the publishing side. The outbox
/// dispatcher derives a topic from the event type alone, so declaring
/// <c>"kafkaTopic": "orders.v2"</c> produced a consumer listening on <c>orders.v2</c> and a publisher
/// writing to <c>order-events</c>. <strong>The generated consumer received nothing, and both halves
/// reported success.</strong>
/// </para>
/// <para>
/// This attribute is what carries the declaration across that gap: the compiler emits it onto the
/// entity, and the outbox queue reads it when enqueuing so the message records where it belongs.
/// Absent, the dispatcher's default naming applies, which is what every schema in this repository
/// relies on.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class KafkaTopicAttribute : Attribute
{
    /// <summary>Gets the topic name domain events for this entity are published to.</summary>
    public string Name { get; }

    /// <summary>Initializes a new instance of the <see cref="KafkaTopicAttribute"/> class.</summary>
    public KafkaTopicAttribute(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A Kafka topic name is required.", nameof(name));
        }

        Name = name;
    }
}
