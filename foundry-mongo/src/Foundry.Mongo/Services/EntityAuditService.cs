using System.Reflection;
using Foundry.Core.Audit;
using Foundry.Core.User;
using Foundry.Core.Entities;
using MongoDB.Bson;

namespace Foundry.Mongo.Services;

/// <summary>
/// Internal service responsible for computing property diffs between old and new entity states,
/// masking sensitive fields in audit diffs, and dispatching audit entries via <see cref="IAuditSink"/>.
/// </summary>
internal sealed class EntityAuditService<T> where T : class, IEntity<ObjectId>
{
    private readonly IAuditSink? _auditSink;
    private readonly ICurrentUserContext? _userContext;

    public EntityAuditService(IAuditSink? auditSink, ICurrentUserContext? userContext)
    {
        _auditSink = auditSink;
        _userContext = userContext;
    }

    /// <summary>
    /// Returns the current operator ID from the user context, falling back to "system".
    /// </summary>
    internal string GetCurrentOperatorId() => _userContext?.OperatorId ?? "system";

    /// <summary>
    /// Whether an audit sink is configured.
    /// </summary>
    internal bool HasAuditSink => _auditSink != null;

    /// <summary>
    /// Computes property diffs between old values and the current state of an entity.
    /// Sensitive fields are automatically masked in the diff output.
    /// </summary>
    internal List<PropertyDiff> ComputeDiffs(Dictionary<string, object?> oldValues, T entityAfter)
    {
        var diffs = new List<PropertyDiff>();
        var properties = EntityEncryptionService<T>.GetCachedProperties();

        foreach (var prop in properties)
        {
            if (!prop.CanRead || prop.Name == "Id") continue;

            var oldVal = oldValues.TryGetValue(prop.Name, out var v) ? v : null;
            var newVal = prop.GetValue(entityAfter);

            if (!object.Equals(oldVal, newVal))
            {
                diffs.Add(new PropertyDiff
                {
                    PropertyName = prop.Name,
                    OldValue = EntityEncryptionService<T>.GetDiffValue(prop, oldVal),
                    NewValue = EntityEncryptionService<T>.GetDiffValue(prop, newVal)
                });
            }
        }

        return diffs;
    }

    /// <summary>
    /// Captures the current property values of an entity into a dictionary for later diff computation.
    /// </summary>
    internal Dictionary<string, object?> CapturePropertyValues(T entity)
    {
        var oldValues = new Dictionary<string, object?>();
        var properties = EntityEncryptionService<T>.GetCachedProperties();
        foreach (var prop in properties)
        {
            if (prop.CanRead) oldValues[prop.Name] = prop.GetValue(entity);
        }
        return oldValues;
    }

    /// <summary>
    /// Writes a single audit log entry for an insert operation.
    /// </summary>
    internal async Task AuditInsertAsync(string operatorId, string entityId, string collectionName, CancellationToken ct)
    {
        if (_auditSink == null) return;

        var entry = AuditLogEntry.ForInsert(
            operatorId,
            typeof(T).FullName ?? typeof(T).Name,
            entityId,
            collectionName);
        await _auditSink.WriteAsync(entry, ct);
    }

    /// <summary>
    /// Writes batch audit log entries for bulk insert operations.
    /// </summary>
    internal async Task AuditBulkInsertAsync(string operatorId, IEnumerable<T> entities, string collectionName, CancellationToken ct)
    {
        if (_auditSink == null) return;

        var auditEntries = entities.Select(entity => AuditLogEntry.ForInsert(
            operatorId,
            typeof(T).FullName ?? typeof(T).Name,
            entity.Id.ToString(),
            collectionName)).ToList();

        await _auditSink.WriteManyAsync(auditEntries, ct);
    }

    /// <summary>
    /// Writes an audit log entry for an update or soft-delete operation, including property diffs.
    /// </summary>
    internal async Task AuditUpdateAsync(
        string operatorId,
        string entityId,
        string collectionName,
        bool isSoftDeletedNow,
        Dictionary<string, object?> oldValues,
        T entityAfter,
        CancellationToken ct)
    {
        if (_auditSink == null) return;

        AuditLogEntry entry;
        if (isSoftDeletedNow)
        {
            entry = AuditLogEntry.ForSoftDelete(
                operatorId,
                typeof(T).FullName ?? typeof(T).Name,
                entityId,
                collectionName);
        }
        else
        {
            var diffs = ComputeDiffs(oldValues, entityAfter);
            entry = AuditLogEntry.ForUpdate(
                operatorId,
                typeof(T).FullName ?? typeof(T).Name,
                entityId,
                collectionName,
                diffs);
        }

        await _auditSink.WriteAsync(entry, ct);
    }

    /// <summary>
    /// Builds an <see cref="AuditLogEntry"/> for an update or soft-delete operation (for bulk batching).
    /// </summary>
    internal AuditLogEntry BuildUpdateAuditEntry(
        string operatorId,
        string entityId,
        string collectionName,
        bool isSoftDeletedNow,
        Dictionary<string, object?> oldValues,
        T entityAfter)
    {
        if (isSoftDeletedNow)
        {
            return AuditLogEntry.ForSoftDelete(
                operatorId,
                typeof(T).FullName ?? typeof(T).Name,
                entityId,
                collectionName);
        }
        else
        {
            var diffs = ComputeDiffs(oldValues, entityAfter);
            return AuditLogEntry.ForUpdate(
                operatorId,
                typeof(T).FullName ?? typeof(T).Name,
                entityId,
                collectionName,
                diffs);
        }
    }

    /// <summary>
    /// Writes an audit log entry for a soft-delete operation.
    /// </summary>
    internal async Task AuditSoftDeleteAsync(string operatorId, string entityId, string collectionName, CancellationToken ct)
    {
        if (_auditSink == null) return;

        var entry = AuditLogEntry.ForSoftDelete(
            operatorId,
            typeof(T).FullName ?? typeof(T).Name,
            entityId,
            collectionName);
        await _auditSink.WriteAsync(entry, ct);
    }

    /// <summary>
    /// Writes an audit log entry for a hard-delete operation.
    /// </summary>
    internal async Task AuditHardDeleteAsync(string operatorId, string entityId, string collectionName, CancellationToken ct)
    {
        if (_auditSink == null) return;

        var entry = AuditLogEntry.ForHardDelete(
            operatorId,
            typeof(T).FullName ?? typeof(T).Name,
            entityId,
            collectionName);
        await _auditSink.WriteAsync(entry, ct);
    }

    /// <summary>
    /// Writes an audit log entry for a restore-from-soft-delete operation.
    /// </summary>
    internal async Task AuditRestoreAsync(string operatorId, string entityId, string collectionName, CancellationToken ct)
    {
        if (_auditSink == null) return;

        var entry = AuditLogEntry.ForRestore(
            operatorId,
            typeof(T).FullName ?? typeof(T).Name,
            entityId,
            collectionName);
        await _auditSink.WriteAsync(entry, ct);
    }

    /// <summary>
    /// Writes a batch of audit entries to the sink.
    /// </summary>
    internal async Task WriteManyAsync(List<AuditLogEntry> entries, CancellationToken ct)
    {
        if (_auditSink == null || entries.Count == 0) return;
        await _auditSink.WriteManyAsync(entries, ct);
    }
}
