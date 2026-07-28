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
            // Dispose must not throw: it runs inside a `using`, often while another exception is
            // already in flight, and throwing here would replace the real failure with this one.
            // The server ends the transaction itself when the session closes, so an abort that
            // fails costs nothing beyond the wait for that.
            try { _session.AbortTransaction(); } catch (Exception) { }
        }
        _session.Dispose();
        _disposed = true;
    }
}
