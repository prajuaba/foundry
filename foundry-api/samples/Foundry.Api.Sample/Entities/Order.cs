using System;
using System.Text.Json.Serialization;
using MongoDB.Bson;
using Foundry.Core.Entities;

namespace Paperclip.OrderingSystem.Domain;

public record Order : BaseEntity<ObjectId>, IVersionable, ISoftDelete
{
    [Indexed]
    public string CustomerId { get; init; } = string.Empty;

    [Indexed(Unique = true)]
    [TextIndexed]
    public required string OrderNumber { get; init; } = string.Empty;

    public decimal TotalAmount { get; init; } = 0;

    public OrderStatus Status { get; init; } = default(OrderStatus);

    [SensitiveData(Protection = ProtectionType.Encrypt)]
    public string SecretToken { get; init; } = string.Empty;

    [SensitiveData(Protection = ProtectionType.Mask, MaskingType = MaskingType.Email)]
    public string UserEmail { get; init; } = string.Empty;

    // Soft-delete bookkeeping is storage state, not part of the API contract. Hiding it also
    // stops a PUT from setting it, which would delete a record via the update route and skip
    // whatever roles the manifest applies to DELETE. The MongoDB driver uses its own BSON
    // mapping and ignores [JsonIgnore], so persistence and filtering are unaffected.
    [Indexed]
    [JsonIgnore]
    public bool IsDeleted { get; init; } = false;

    [JsonIgnore]
    public DateTime? DeletedAt { get; init; }
}
