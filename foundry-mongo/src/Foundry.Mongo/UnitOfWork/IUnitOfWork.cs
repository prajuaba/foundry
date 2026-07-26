using System;
using System.Threading;
using System.Threading.Tasks;
using MongoDB.Driver;

namespace Foundry.Mongo.UnitOfWork;

/// <summary>
/// Defines the contract for executing multiple repository operations within an atomic MongoDB session transaction.
/// </summary>
public interface IUnitOfWork : IDisposable
{
    /// <summary>The active MongoDB client session handle.</summary>
    IClientSessionHandle Session { get; }

    /// <summary>Commits all changes made within the transaction boundary.</summary>
    Task CommitAsync(CancellationToken ct = default);

    /// <summary>Aborts/rolls back all changes made within the transaction boundary.</summary>
    Task AbortAsync(CancellationToken ct = default);
}
