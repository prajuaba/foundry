namespace Foundry.Core.Entities;

/// <summary>
/// Marker interface for entities that support soft-delete.
/// When applied, the DAL automatically adds a query filter to exclude IsDeleted=true documents.
/// Implements DeleteAsync will set IsDeleted and DeletedAt instead of removing the document.
/// </summary>
public interface ISoftDelete
{
    /// <summary>True if this entity has been soft-deleted.</summary>
    bool IsDeleted { get; init; }

    /// <summary>UTC timestamp when the soft-delete was applied. Null when active.</summary>
    DateTime? DeletedAt { get; init; }
}
