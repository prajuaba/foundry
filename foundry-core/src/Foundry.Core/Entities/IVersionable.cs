namespace Foundry.Core.Entities;

/// <summary>
/// Marker interface indicating that the entity supports historical data versioning/revisions.
/// Every mutation (Insert/Update/Delete) automatically creates a snapshot record in the shadow history collection.
/// </summary>
public interface IVersionable
{
}
