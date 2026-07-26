using System;

namespace Foundry.Core.Outbox;

/// <summary>
/// A generic domain event published to the outbox whenever an entity is mutated (inserted, updated, or deleted).
/// </summary>
/// <typeparam name="T">Type of the mutated entity.</typeparam>
public class EntityMutationEvent<T>
{
    /// <summary>Gets or sets the type of mutation (Insert, Update, Delete).</summary>
    public string MutationType { get; set; } = string.Empty;

    /// <summary>Gets or sets the target entity data at the time of the event.</summary>
    public T Entity { get; set; } = default!;

    /// <summary>Gets or sets the timestamp when the event occurred.</summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
