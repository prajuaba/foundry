using Foundry.Core.Entities;

namespace Foundry.Core.Entities;

/// <summary>
/// Base concrete entity providing default implementations for standard DAL lifecycle fields and helper methods.
/// Entities that include soft-delete semantics should also implement ISoftDelete (partial class pattern).
/// </summary>
public abstract record BaseEntity<TId> : IEntity<TId> where TId : IEquatable<TId?>
{
    /// <inheritdoc />
    public required TId Id { get; init; }

    /// <inheritdoc />
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <inheritdoc />
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <inheritdoc />
    public int Version { get; set; }

    /// <summary>
    /// Called by the DAL to stamp the UpdatedAtUtc field during an update operation.
    /// </summary>
    public void OnUpdate() => UpdatedAtUtc = DateTime.UtcNow;
}
