using System;
using MongoDB.Bson;
using Foundry.Core.Entities;

namespace Foundry.Api.Template.Domain;

public record TodoItem : BaseEntity<ObjectId>
{
    [Indexed(Unique = true)]
    public string Title { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public bool IsCompleted { get; init; } = false;

    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    [SensitiveData(Protection = ProtectionType.Mask)]
    public string OwnerEmail { get; init; } = string.Empty;
}
