using System;
using Foundry.Core.Entities;
using Foundry.Mongo.Repositories;
using MongoDB.Bson;
using MongoDB.Driver;
using NSubstitute;
using Xunit;

namespace Foundry.Mongo.Tests;

/// <summary>
/// Item 2 (non-string masked properties were computed and thrown away, silently returning the
/// original value) and Item 3 (a StateProperty so a masked default is distinguishable from a real
/// one) exercised without a live MongoDB, the same way <see cref="SensitiveDataMaskingTests"/> does.
/// </summary>
public class MaskingNonStringAndStatePropertyTests
{
    public enum MaskState { None, Masked }

    public record FinancialRecord : BaseEntity<ObjectId>
    {
        public string Reference { get; set; } = string.Empty;

        [SensitiveData(Protection = ProtectionType.Mask, Category = "financial", StateProperty = nameof(AmountState))]
        public decimal Amount { get; set; }

        public MaskState AmountState { get; set; } = MaskState.None;

        [SensitiveData(Protection = ProtectionType.Mask, Category = "financial", StateProperty = "NoSuchProperty")]
        public decimal Balance { get; set; }

        [SensitiveData(Protection = ProtectionType.Mask, Category = "financial")]
        public int Score { get; set; }
    }

    [Fact]
    public void ADecimalPropertyIsZeroedForACallerWhoMustNotSeeIt()
    {
        var mockDb = Substitute.For<IMongoDatabase>();
        var repository = new Repository<FinancialRecord>(mockDb, userContext: new TestUserContext());

        var entity = new FinancialRecord
        {
            Id = ObjectId.GenerateNewId(),
            Reference = "REF-123",
            Amount = 12345.67m,
            AmountState = MaskState.None,
            Score = 0
        };

        var masked = repository.MaskSensitiveFields(entity);

        Assert.Equal(0m, masked.Amount);
        Assert.Equal(12345.67m, entity.Amount); // Masking clones; it does not mutate the original
    }

    [Fact]
    public void ADecimalPropertyIsLeftAloneForACallerWhoMaySeeIt()
    {
        var mockDb = Substitute.For<IMongoDatabase>();
        var repository = new Repository<FinancialRecord>(mockDb, userContext: new TestUserContext("view:financial"));

        var entity = new FinancialRecord
        {
            Id = ObjectId.GenerateNewId(),
            Reference = "REF-123",
            Amount = 12345.67m,
            AmountState = MaskState.None,
            Score = 0
        };

        var masked = repository.MaskSensitiveFields(entity);

        Assert.Equal(12345.67m, masked.Amount);
        Assert.Equal(12345.67m, entity.Amount);
    }

    [Fact]
    public void MaskingADecimalAlsoZeroesAnIntPropertyIndependently()
    {
        var mockDb = Substitute.For<IMongoDatabase>();
        var repository = new Repository<FinancialRecord>(mockDb, userContext: new TestUserContext());

        var entity = new FinancialRecord
        {
            Id = ObjectId.GenerateNewId(),
            Reference = "REF-123",
            Score = 42,
            Amount = 0m,
            AmountState = MaskState.None
        };

        var masked = repository.MaskSensitiveFields(entity);

        Assert.Equal(0, masked.Score);
        Assert.Equal(42, entity.Score);
    }

    [Fact]
    public void MaskingSetsTheNamedEnumStatePropertyToMasked()
    {
        var mockDb = Substitute.For<IMongoDatabase>();
        var repository = new Repository<FinancialRecord>(mockDb, userContext: new TestUserContext());

        var entity = new FinancialRecord
        {
            Id = ObjectId.GenerateNewId(),
            Reference = "REF-123",
            Amount = 500m,
            AmountState = MaskState.None,
            Score = 0
        };

        var masked = repository.MaskSensitiveFields(entity);

        Assert.Equal(0m, masked.Amount);
        Assert.Equal(MaskState.Masked, masked.AmountState);
    }

    [Fact]
    public void AnEntitledCallerDoesNotHaveTheStatePropertySetEither()
    {
        var mockDb = Substitute.For<IMongoDatabase>();
        var repository = new Repository<FinancialRecord>(mockDb, userContext: new TestUserContext("view:financial"));

        var entity = new FinancialRecord
        {
            Id = ObjectId.GenerateNewId(),
            Reference = "REF-123",
            Amount = 500m,
            AmountState = MaskState.None,
            Score = 0
        };

        var masked = repository.MaskSensitiveFields(entity);

        Assert.Equal(500m, masked.Amount);
        Assert.Equal(MaskState.None, masked.AmountState); // Never masked for this caller, so untouched
    }

    [Fact]
    public void AStatePropertyNamingANonexistentPropertyDoesNotThrow()
    {
        var mockDb = Substitute.For<IMongoDatabase>();
        var repository = new Repository<FinancialRecord>(mockDb, userContext: new TestUserContext());

        var entity = new FinancialRecord
        {
            Id = ObjectId.GenerateNewId(),
            Reference = "REF-123",
            Balance = 99.99m,
            Amount = 0m,
            AmountState = MaskState.None,
            Score = 0
        };

        var masked = repository.MaskSensitiveFields(entity);

        Assert.Equal(0m, masked.Balance);
    }
}
