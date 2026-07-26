using System;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Foundry.Core.Entities;

/// <summary>
/// Represents a historical snapshot revision of a document.
/// </summary>
public sealed class EntityRevision
{
    [BsonId]
    public ObjectId Id { get; set; } = ObjectId.GenerateNewId();

    /// <summary>The ID of the original entity as a string.</summary>
    public string EntityId { get; set; } = string.Empty;

    /// <summary>The version number of the document at this revision.</summary>
    public int Version { get; set; }

    /// <summary>The raw BSON data snapshot of the document at this revision.</summary>
    public BsonDocument Data { get; set; } = new();

    /// <summary>The UTC timestamp when this revision was recorded.</summary>
    public DateTime ChangedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>The ID of the operator who triggered the change.</summary>
    public string ChangedBy { get; set; } = string.Empty;

    /// <summary>The action that generated this revision (e.g., "Insert", "Update", "Delete", "Restore").</summary>
    public string Action { get; set; } = string.Empty;
}
