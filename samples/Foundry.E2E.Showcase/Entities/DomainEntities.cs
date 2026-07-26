using System;
using MongoDB.Bson;
using Foundry.Core.Entities;

namespace Foundry.E2E.Showcase.Entities
{
    public enum CustomerTier
    {
        Standard,
        Silver,
        Gold,
        Platinum
    }

    public enum OrderStatus
    {
        Pending,
        Approved,
        Shipped,
        Cancelled
    }

    public record Customer : BaseEntity<ObjectId>, ISoftDelete
    {
        [SensitiveData(Protection = ProtectionType.Encrypt, MaskingType = MaskingType.Email)]
        public string Email { get; init; } = string.Empty;

        public string FullName { get; init; } = string.Empty;

        public decimal CreditLimit { get; init; }

        public CustomerTier Tier { get; init; }

        public bool IsDeleted { get; init; }
        public DateTime? DeletedAt { get; init; }
    }

    public record Product : BaseEntity<ObjectId>, ISoftDelete
    {
        public string Sku { get; init; } = string.Empty;

        public string Name { get; init; } = string.Empty;

        public decimal UnitPrice { get; init; }

        public int StockQuantity { get; init; }

        public bool IsDeleted { get; init; }
        public DateTime? DeletedAt { get; init; }
    }

    public record Order : BaseEntity<ObjectId>, ISoftDelete
    {
        public ObjectId CustomerId { get; init; }

        public string OrderNumber { get; init; } = string.Empty;

        public decimal TotalAmount { get; init; }

        public OrderStatus Status { get; init; }

        public DateTime OrderDate { get; init; } = DateTime.UtcNow;

        public bool IsDeleted { get; init; }
        public DateTime? DeletedAt { get; init; }
    }
}
