namespace Foundry.Mongo.UnitOfWork;

/// <summary>
/// Defines a factory for spawning atomic transactional units of work.
/// </summary>
public interface IUnitOfWorkFactory
{
    /// <summary>Spawns a new atomic unit of work with a started transaction.</summary>
    IUnitOfWork Create();
}
