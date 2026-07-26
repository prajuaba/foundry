using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundry.Core.Audit;
using Foundry.Core.Entities;
using Foundry.Mongo.Repositories;
using MongoDB.Bson;
using MongoDB.Driver;
using NSubstitute;
using Xunit;

namespace Foundry.Mongo.Tests;

public class SensitiveDataMaskingTests
{
    public record UserProfile : BaseEntity<ObjectId>
    {
        public string Username { get; set; } = string.Empty;

        [SensitiveData(MaskingType = MaskingType.Full)]
        public string PasswordHash { get; set; } = string.Empty;

        [SensitiveData(MaskingType = MaskingType.Partial, PreserveCount = 4)]
        public string CreditCardNumber { get; set; } = string.Empty;

        [SensitiveData(MaskingType = MaskingType.Email)]
        public string EmailAddress { get; set; } = string.Empty;
    }

    [Fact]
    public void MaskSensitiveFields_CorrectlyMasksPropertiesOnClone()
    {
        // Arrange
        var mockDb = Substitute.For<IMongoDatabase>();
        var repository = new Repository<UserProfile>(mockDb);

        var profile = new UserProfile
        {
            Id = ObjectId.GenerateNewId(),
            Username = "bob123",
            PasswordHash = "SuperSecretHashValue123",
            CreditCardNumber = "1111222233334444",
            EmailAddress = "bob.builder@domain.com"
        };

        // Act
        var masked = repository.MaskSensitiveFields(profile);

        // Assert - Original is untouched
        Assert.Equal("SuperSecretHashValue123", profile.PasswordHash);
        Assert.Equal("1111222233334444", profile.CreditCardNumber);
        Assert.Equal("bob.builder@domain.com", profile.EmailAddress);

        // Assert - Clone is masked
        Assert.Equal("bob123", masked.Username);
        Assert.Equal("***************", masked.PasswordHash); // Full masking caps at 15 chars
        Assert.Equal("**********4444", masked.CreditCardNumber);     // Partial masking preserves last 4
        Assert.Equal("b*****r@domain.com", masked.EmailAddress);  // Email masking preserves username first/last and domain
    }

    [Fact]
    public async Task UpdateAsync_AutomaticallyMasksDiffsInAuditLog()
    {
        // Arrange
        var mockDb = Substitute.For<IMongoDatabase>();
        var mockCollection = Substitute.For<IMongoCollection<UserProfile>>();
        mockCollection.CollectionNamespace.Returns(new CollectionNamespace(new DatabaseNamespace("TestDb"), "UserProfiles"));
        mockCollection.Database.Returns(mockDb);
        mockDb.GetCollection<UserProfile>(Arg.Any<string>()).Returns(mockCollection);

        var entityId = ObjectId.Parse("507f1f77bcf86cd799439011");
        
        var existingProfile = new UserProfile
        {
            Id = entityId,
            Username = "bob123",
            PasswordHash = "OldSecretHash",
            CreditCardNumber = "4111111111111111",
            EmailAddress = "old.email@domain.com",
            Version = 1
        };

        // Mock pre-image fetch
        var cursor = new TestAsyncCursor<UserProfile>(existingProfile);
        mockCollection.FindAsync(
            Arg.Any<FilterDefinition<UserProfile>>(),
            Arg.Any<FindOptions<UserProfile, UserProfile>>(),
            Arg.Any<CancellationToken>()
        ).Returns(Task.FromResult<IAsyncCursor<UserProfile>>(cursor));

        // Mock ReplaceOne success
        var replaceResult = Substitute.For<ReplaceOneResult>();
        replaceResult.MatchedCount.Returns(1);
        mockCollection.ReplaceOneAsync(
            Arg.Any<FilterDefinition<UserProfile>>(),
            Arg.Any<UserProfile>(),
            Arg.Any<ReplaceOptions>(),
            Arg.Any<CancellationToken>()
        ).Returns(Task.FromResult(replaceResult));

        var auditSink = new InMemoryAuditSink();
        var repository = new Repository<UserProfile>(mockDb, auditSink);

        var updatedProfile = new UserProfile
        {
            Id = entityId,
            Username = "bob123", // unchanged
            PasswordHash = "NewSecretHash", // changed
            CreditCardNumber = "5222222222222222", // changed
            EmailAddress = "new.email@domain.com", // changed
            Version = 1
        };

        // Act
        await repository.UpdateAsync(updatedProfile);

        // Assert - Audit Sink contains masked diff values
        var entries = auditSink.GetEntries();
        Assert.Single(entries);
        var entry = entries[0];
        
        // Assert password diff is fully masked
        Assert.Contains(entry.PropertyDiffs, d => d.PropertyName == "PasswordHash");
        var passwordDiff = entry.PropertyDiffs.First(d => d.PropertyName == "PasswordHash");
        Assert.Equal("*************", passwordDiff.OldValue);
        Assert.Equal("*************", passwordDiff.NewValue);

        // Assert credit card is partially masked
        Assert.Contains(entry.PropertyDiffs, d => d.PropertyName == "CreditCardNumber");
        var cardDiff = entry.PropertyDiffs.First(d => d.PropertyName == "CreditCardNumber");
        Assert.Equal("**********1111", cardDiff.OldValue);
        Assert.Equal("**********2222", cardDiff.NewValue);

        // Assert email is email-masked
        Assert.Contains(entry.PropertyDiffs, d => d.PropertyName == "EmailAddress");
        var emailDiff = entry.PropertyDiffs.First(d => d.PropertyName == "EmailAddress");
        Assert.Equal("o*****l@domain.com", emailDiff.OldValue);
        Assert.Equal("n*****l@domain.com", emailDiff.NewValue);
    }
}
