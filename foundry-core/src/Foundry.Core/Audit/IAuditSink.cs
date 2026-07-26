using System.Collections.Concurrent;
using Foundry.Core.Audit;

namespace Foundry.Core.Audit;

/// <summary>
/// Contract for audit logging sinks. Implementations may write to MongoDB, Elasticsearch, files, Azure Blob Storage, etc.
/// The DAL calls WriteAsync after every mutating operation (insert, update, delete).
/// If auditing is not properly configured, mutations will fail with a InvalidOperationException.
/// </summary>
public interface IAuditSink
{
    /// <summary>
    /// Writes a single audit log entry asynchronously.
    /// Implementations must ensure the entry is immutable — once written it cannot be modified or deleted.
    /// </summary>
    /// <param name="entry">The audit entry to write.</param>
    /// <param name="ct">Cancellation token for async operations.</param>
    /// <returns>A task representing the async operation. May throw if writing fails.</returns>
    public Task WriteAsync(AuditLogEntry entry, CancellationToken ct = default);

    /// <summary>
    /// Writes a batch of audit entries asynchronously. Implementations may buffer or stream this efficiently.
    /// </summary>
    /// <param name="entries">The audit entries to write.</param>
    /// <param name="ct">Cancellation token for async operations.</param>
    public Task WriteManyAsync(IReadOnlyList<AuditLogEntry> entries, CancellationToken ct = default);
}

/// <summary>
/// In-memory audit sink for testing and local development. Appends entries to a thread-safe ConcurrentBag — no disk or network costs.
/// Not suitable for production use since audit data is not durable (lost on restart).
/// Provides enumeration over captured entries via GetEntries() and Clear() for test isolation.
/// </summary>
public sealed class InMemoryAuditSink : IAuditSink
{
    private readonly ConcurrentBag<AuditLogEntry> _cache = new();

    /// <inheritdoc />
    public Task WriteAsync(AuditLogEntry entry, CancellationToken ct = default)
    {
        CheckConfiguration(entry);
        _cache.Add(entry);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task WriteManyAsync(IReadOnlyList<AuditLogEntry> entries, CancellationToken ct = default)
    {
        foreach (var entry in entries)
            CheckConfiguration(entry);
        
        foreach (var entry in entries)
            _cache.Add(entry);
        
        return Task.CompletedTask;
    }

    /// <summary>
    /// Returns all captured audit log entries as an immutable snapshot.
    /// Each call returns a fresh copy to prevent external mutation.
    /// </summary>
    public IReadOnlyList<AuditLogEntry> GetEntries() => [.. _cache];

    /// <summary>
    /// Clears all captured entries. Useful in test setup/teardown for isolation between tests.
    /// No-op if no entries exist or already empty.
    /// </summary>
    public void Clear() => _cache.Clear();

    private static void CheckConfiguration(AuditLogEntry entry)
    {
        if (string.IsNullOrEmpty(entry.OperatorId))
            throw new InvalidOperationException("Audit sink requires a configured OperatorId in AuditLogEntry");
        
        if (string.IsNullOrEmpty(entry.EntityType))
            throw new InvalidOperationException("Audit log entry must include entity type for audit purposes");
    }
}
