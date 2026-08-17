using System;
using System.Collections.Generic;
using System.Linq;
using MongoDB.Bson;

namespace Foundry.Core.Audit;

/// <summary>
/// Action type enum for audit trail classification.
/// </summary>
public enum AuditAction : byte
{
    /// <summary>Entity was newly inserted.</summary>
    Inserted = 1,

    /// <summary>Entity fields were modified.</summary>
    Updated = 2,

    /// <summary>Entity was hard-deleted from the collection.</summary>
    DeletedHard = 3,

    /// <summary>Entity was soft-deleted (IsDeleted marked).</summary>
    DeletedSoft = 4,

    /// <summary>Entity was restored from soft-deleted state.</summary>
    Restored = 5,

    /// <summary>Entity was read/accessed.</summary>
    Read = 6,
}

/// <summary>
/// Immutable audit log entry capturing the full context of a mutating data access operation.
/// Includes who (operator), when (UTC timestamp), what (entity/collection), and how (property diffs).
/// </summary>
public sealed record AuditLogEntry
{
    /// <summary>Unique MongoDB ObjectId for this audit event.</summary>
    public ObjectId Id { get; init; } = ObjectId.GenerateNewId();

    /// <summary>System-issued identifier of the operator performing the action.</summary>
    public required string OperatorId { get; init; }

    /// <summary>Display name of the operator performing the action.</summary>
    public string? OperatorName { get; init; }

    /// <summary>UTC timestamp when the audit entry was created (ISO-8601).</summary>
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;

    /// <summary>Fully qualified type name of the entity being audited.</summary>
    public required string EntityType { get; init; }

    /// <summary>The primary key value (as string) of the affected entity document.</summary>
    public required string EntityId { get; init; }

    /// <summary>MongoDB collection name where the entity is stored.</summary>
    public required string CollectionName { get; init; }

    /// <summary>List of property diffs detected during the mutation. Null/empty for inserts and deletes.</summary>
    public IReadOnlyList<PropertyDiff> PropertyDiffs { get; init; } = [];

    /// <summary>Type of audit event (Inserted, Updated, DeletedHard, DeletedSoft, Restored, Read).</summary>
    public required AuditAction Action { get; init; }

    /// <summary>
    /// Tenant the audited entity belongs to, or null when the write happened outside any tenant
    /// scope (system tasks, migrations, single-tenant deployments).
    /// </summary>
    /// <remarks>
    /// Nullable on purpose. Entries written before this field existed have no tenant, and a
    /// tenant-scoped read must exclude them rather than guess -- showing an unattributed entry to
    /// whoever asks first is the failure this field exists to prevent.
    /// </remarks>
    public string? TenantId { get; init; }

    /// <summary>Number of properties changed by this operation.</summary>
    public int ChangeCount => PropertyDiffs.Count(p => p.HasChanged);

    /// <summary>True when no properties actually had their values changed (null check on diffs).</summary>
    public bool HasActualChanges => PropertyDiffs.Any(p => p.HasChanged);

    /// <summary>
    /// Creates an audit entry for an insert operation with no diff tracking.
    /// </summary>
    public static AuditLogEntry ForInsert(string operatorId, string entityType, string entityId, string collectionName) =>
        new()
        {
            OperatorId = operatorId,
            EntityType = entityType,
            EntityId = entityId,
            CollectionName = collectionName,
            Action = AuditAction.Inserted,
        };

    /// <summary>
    /// Creates an audit entry for a soft-delete operation.
    /// </summary>
    public static AuditLogEntry ForSoftDelete(string operatorId, string entityType, string entityId, string collectionName) =>
        new()
        {
            OperatorId = operatorId,
            EntityType = entityType,
            EntityId = entityId,
            CollectionName = collectionName,
            Action = AuditAction.DeletedSoft,
            PropertyDiffs = [new PropertyDiff { PropertyName = "IsDeleted", OldValue = false, NewValue = true }],
        };

    /// <summary>
    /// Creates an audit entry for a hard-delete operation.
    /// </summary>
    public static AuditLogEntry ForHardDelete(string operatorId, string entityType, string entityId, string collectionName) =>
        new()
        {
            OperatorId = operatorId,
            EntityType = entityType,
            EntityId = entityId,
            CollectionName = collectionName,
            Action = AuditAction.DeletedHard,
        };

    /// <summary>
    /// Creates an audit entry for an update operation with computed diffs.
    /// </summary>
    public static AuditLogEntry ForUpdate(string operatorId, string entityType, string entityId, string collectionName, IReadOnlyList<PropertyDiff> diffs) =>
        new()
        {
            OperatorId = operatorId,
            EntityType = entityType,
            EntityId = entityId,
            CollectionName = collectionName,
            Action = AuditAction.Updated,
            PropertyDiffs = diffs ?? [],
        };

    /// <summary>
    /// Creates an audit entry for a restore operation.
    /// </summary>
    public static AuditLogEntry ForRestore(string operatorId, string entityType, string entityId, string collectionName) =>
        new()
        {
            OperatorId = operatorId,
            EntityType = entityType,
            EntityId = entityId,
            CollectionName = collectionName,
            Action = AuditAction.Restored,
            PropertyDiffs = [new PropertyDiff { PropertyName = "IsDeleted", OldValue = true, NewValue = false }]
        };

    /// <summary>
    /// Creates an audit entry for a read operation.
    /// </summary>
    public static AuditLogEntry ForRead(string operatorId, string entityType, string entityId, string collectionName) =>
        new()
        {
            OperatorId = operatorId,
            EntityType = entityType,
            EntityId = entityId,
            CollectionName = collectionName,
            Action = AuditAction.Read,
        };

    /// <summary>
    /// Returns a summary string representation for quick debugging/logging.
    /// </summary>
    public override string ToString() => $"[{TimestampUtc:O}] {OperatorId} [{Action}] {EntityType}/{EntityId}@{CollectionName} ({ChangeCount} changes)";
}
