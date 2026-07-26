using Foundry.Core.Entities;

namespace Foundry.Core.Entities;

/// <summary>
/// Concrete soft-delete marker that can be mixed into any entity type via partial class pattern.
/// Usage: public sealed partial class Order : BaseEntity&lt;ObjectId&gt;, ISoftDelete { }
/// The DAL will automatically check for this interface during delete operations.
/// </summary>
public readonly record struct SoftDeleteMarker : ISoftDelete
{
    public bool IsDeleted { get; init; }
    public DateTime? DeletedAt { get; init; }
}
