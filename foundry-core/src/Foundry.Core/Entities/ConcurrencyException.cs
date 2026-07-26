using System;

namespace Foundry.Core.Entities;

/// <summary>
/// Exception thrown when an optimistic concurrency check fails during an entity update operation.
/// Indicates that the document was modified by another transaction since it was read.
/// </summary>
public sealed class ConcurrencyException : Exception
{
    public string EntityId { get; }
    public string CollectionName { get; }

    public ConcurrencyException(string entityId, string collectionName, string message) 
        : base(message)
    {
        EntityId = entityId;
        CollectionName = collectionName;
    }

    public ConcurrencyException(string entityId, string collectionName, string message, Exception innerException) 
        : base(message, innerException)
    {
        EntityId = entityId;
        CollectionName = collectionName;
    }
}
