using System;
using System.Linq;
using Foundry.Core.Attributes;

namespace Foundry.Core.Outbox;

/// <summary>
/// Reads the topic an event's subject declares, if it declares one.
/// </summary>
/// <remarks>
/// Resolved when the message is enqueued rather than when it is published, so the destination is
/// recorded on the row alongside the payload. A message that has been sitting in the outbox across a
/// deployment then publishes where it was addressed, not where the current build would have guessed.
/// </remarks>
public static class KafkaTopicDeclaration
{
    /// <summary>
    /// The declared topic for an event type, or null to leave the dispatcher's default naming to apply.
    /// </summary>
    /// <remarks>
    /// An event is named for what it is about: <c>EntityMutationEvent&lt;Order&gt;</c> is an event
    /// about <c>Order</c>, so the attribute is looked for on <c>Order</c>. This matches how the
    /// dispatcher derives a default name, where the first type argument also wins.
    /// </remarks>
    public static string? For(Type eventType)
    {
        ArgumentNullException.ThrowIfNull(eventType);

        return Declared(Subject(eventType));
    }

    /// <summary>
    /// Whether the entity this event is about opted into the outbox.
    /// </summary>
    /// <remarks>
    /// Answered from <see cref="KafkaOutboxAttribute"/> rather than from the topic, because an
    /// entity may opt in without naming one and let the dispatcher derive it. Reading the topic as
    /// the opt-in signal would publish everything that declared a topic and nothing that did not,
    /// which is the wrong question asked of the wrong field.
    /// </remarks>
    public static bool IsEnabledFor(Type eventType)
    {
        ArgumentNullException.ThrowIfNull(eventType);

        return Subject(eventType)
            .GetCustomAttributes(typeof(KafkaOutboxAttribute), inherit: true)
            .Length > 0;
    }

    private static Type Subject(Type eventType)
        => eventType.IsGenericType && eventType.GetGenericArguments().Length > 0
            ? eventType.GetGenericArguments()[0]
            : eventType;

    private static string? Declared(Type type)
        => type.GetCustomAttributes(typeof(KafkaTopicAttribute), inherit: true)
            .OfType<KafkaTopicAttribute>()
            .FirstOrDefault()?.Name;
}
