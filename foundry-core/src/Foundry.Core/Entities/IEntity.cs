namespace Foundry.Core.Entities;

/// <summary>
/// Strongly-typed entity contract for the DAL. All domain entities must implement this to ensure consistent document structure across collections.
/// </summary>
public interface IEntity<TId> where TId : IEquatable<TId?>
{
    /// <summary>The unique document identifier (Mongo _id).</summary>
    public TId Id { get; init; }

    /// <summary>ISO-8601 UTC creation timestamp.</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>ISO-8601 UTC last-updated timestamp.</summary>
    public DateTime UpdatedAtUtc { get; set; }

    /// <summary>OSSC version field. Incremented on each update.</summary>
    public int Version { get; set; }
}
