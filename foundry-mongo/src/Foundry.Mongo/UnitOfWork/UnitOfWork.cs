using System;
using System.Threading;
using System.Threading.Tasks;
using MongoDB.Driver;

namespace Foundry.Mongo.UnitOfWork;

/// <summary>
/// Lightweight transaction coordinator that delegates to a MongoDB IClientSessionHandle.
/// </summary>
public sealed class UnitOfWork : IUnitOfWork
{
    private readonly IClientSessionHandle _session;
    private bool _committed;
    private bool _disposed;

    public IClientSessionHandle Session => _session;

    public UnitOfWork(IMongoClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _session = client.StartSession();
        _session.StartTransaction();
    }

    public async Task CommitAsync(CancellationToken ct = default)
    {
        if (_committed || _disposed) return;
        await _session.CommitTransactionAsync(ct);
        _committed = true;
    }

    public async Task AbortAsync(CancellationToken ct = default)
    {
        if (_committed || _disposed) return;
        await _session.AbortTransactionAsync(ct);
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (!_committed && _session.IsInTransaction)
        {
            try { _session.AbortTransaction(); } catch { /* Suppress */ }
        }
        _session.Dispose();
        _disposed = true;
    }
}
