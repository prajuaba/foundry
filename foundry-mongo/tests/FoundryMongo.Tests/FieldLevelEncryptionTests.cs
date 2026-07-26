using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Foundry.Core.Audit;
using Foundry.Core.Entities;
using Foundry.Core.Security;
using Foundry.Mongo.Repositories;
using MongoDB.Bson;
using MongoDB.Driver;
using NSubstitute;
using Xunit;

namespace Foundry.Mongo.Tests;

public class FieldLevelEncryptionTests
{
    public record SecuredCustomer : BaseEntity<ObjectId>, IVersionable
    {
        public string Name { get; set; } = string.Empty;

        [SensitiveData(Protection = ProtectionType.Encrypt)]
        public string TaxId { get; set; } = string.Empty;

        [SensitiveData(Protection = ProtectionType.Encrypt)]
        public string SecretCode { get; set; } = string.Empty;
    }

    private static readonly string EncryptionKey = Convert.ToBase64String(Encoding.UTF8.GetBytes("12345678901234567890123456789012")); // 32 bytes

    [Fact]
    public async Task InsertAsync_EncryptsFieldsBeforeWriting_And_DecryptsOnRead()
    {
        // Arrange
        var mockDb = Substitute.For<IMongoDatabase>();
        var mockCollection = Substitute.For<IMongoCollection<SecuredCustomer>>();
        mockCollection.CollectionNamespace.Returns(new CollectionNamespace(new DatabaseNamespace("TestDb"), "SecuredCustomers"));
        mockCollection.Database.Returns(mockDb);
        mockDb.GetCollection<SecuredCustomer>(Arg.Any<string>()).Returns(mockCollection);

        var encryptionProvider = new AesEncryptionProvider(EncryptionKey);
        var repository = new Repository<SecuredCustomer>(mockDb, null, null, encryptionProvider);

        var customer = new SecuredCustomer
        {
            Id = ObjectId.GenerateNewId(),
            Name = "Alice Corp",
            TaxId = "TAX-999-111",
            SecretCode = "TopSecretPassword"
        };

        SecuredCustomer? capturedWriteEntity = null;

        // Act - Insert
        mockCollection.InsertOneAsync(
            Arg.Do<SecuredCustomer>(c => capturedWriteEntity = c),
            Arg.Any<InsertOneOptions>(),
            Arg.Any<CancellationToken>()
        ).Returns(Task.CompletedTask);

        await repository.InsertAsync(customer);

        // Assert - Inserted entity had encrypted properties
        Assert.NotNull(capturedWriteEntity);
        Assert.Equal("Alice Corp", capturedWriteEntity.Name);
        Assert.NotEqual("TAX-999-111", capturedWriteEntity.TaxId);
        Assert.NotEqual("TopSecretPassword", capturedWriteEntity.SecretCode);

        // Decrypt captured value using our key to verify it is encrypted correctly
        var decryptedTaxId = encryptionProvider.Decrypt(capturedWriteEntity.TaxId);
        var decryptedCode = encryptionProvider.Decrypt(capturedWriteEntity.SecretCode);
        Assert.Equal("TAX-999-111", decryptedTaxId);
        Assert.Equal("TopSecretPassword", decryptedCode);

        // Act - Mock database finding the encrypted document and read it back
        var dbRecord = new SecuredCustomer
        {
            Id = customer.Id,
            Name = "Alice Corp",
            TaxId = capturedWriteEntity.TaxId, // Ciphertext in DB
            SecretCode = capturedWriteEntity.SecretCode, // Ciphertext in DB
            Version = 1
        };

        var cursor = new TestAsyncCursor<SecuredCustomer>(dbRecord);
        mockCollection.FindAsync(
            Arg.Any<FilterDefinition<SecuredCustomer>>(),
            Arg.Any<FindOptions<SecuredCustomer, SecuredCustomer>>(),
            Arg.Any<CancellationToken>()
        ).Returns(Task.FromResult<IAsyncCursor<SecuredCustomer>>(cursor));

        var fetched = await repository.GetByIdAsync(customer.Id);

        // Assert - Retrieved document is decrypted automatically
        Assert.NotNull(fetched);
        Assert.Equal("Alice Corp", fetched.Name);
        Assert.Equal("TAX-999-111", fetched.TaxId);
        Assert.Equal("TopSecretPassword", fetched.SecretCode);
    }

    [Fact]
    public async Task UpdateAsync_EncryptsDatabaseWrite_And_MasksAuditTrail()
    {
        // Arrange
        var mockDb = Substitute.For<IMongoDatabase>();
        var mockCollection = Substitute.For<IMongoCollection<SecuredCustomer>>();
        mockCollection.CollectionNamespace.Returns(new CollectionNamespace(new DatabaseNamespace("TestDb"), "SecuredCustomers"));
        mockCollection.Database.Returns(mockDb);
        mockDb.GetCollection<SecuredCustomer>(Arg.Any<string>()).Returns(mockCollection);

        var encryptionProvider = new AesEncryptionProvider(EncryptionKey);
        var auditSink = new InMemoryAuditSink();
        var repository = new Repository<SecuredCustomer>(mockDb, auditSink, null, encryptionProvider);

        var entityId = ObjectId.Parse("507f1f77bcf86cd799439011");

        // Old cipher texts
        var oldTaxIdCipher = encryptionProvider.Encrypt("TAX-OLD");
        var oldSecretCipher = encryptionProvider.Encrypt("SECRET-OLD");

        var databaseCustomer = new SecuredCustomer
        {
            Id = entityId,
            Name = "Alice Corp",
            TaxId = oldTaxIdCipher,
            SecretCode = oldSecretCipher,
            Version = 1
        };

        // Mock pre-image fetch
        var cursor = new TestAsyncCursor<SecuredCustomer>(databaseCustomer);
        mockCollection.FindAsync(
            Arg.Any<FilterDefinition<SecuredCustomer>>(),
            Arg.Any<FindOptions<SecuredCustomer, SecuredCustomer>>(),
            Arg.Any<CancellationToken>()
        ).Returns(Task.FromResult<IAsyncCursor<SecuredCustomer>>(cursor));

        // Mock ReplaceOne success
        var replaceResult = Substitute.For<ReplaceOneResult>();
        replaceResult.MatchedCount.Returns(1);

        SecuredCustomer? capturedUpdateEntity = null;
        mockCollection.ReplaceOneAsync(
            Arg.Any<FilterDefinition<SecuredCustomer>>(),
            Arg.Do<SecuredCustomer>(c => capturedUpdateEntity = c),
            Arg.Any<ReplaceOptions>(),
            Arg.Any<CancellationToken>()
        ).Returns(Task.FromResult(replaceResult));

        var updatedCustomer = new SecuredCustomer
        {
            Id = entityId,
            Name = "Alice Corp",
            TaxId = "TAX-NEW", // Plaintext for update
            SecretCode = "SECRET-NEW", // Plaintext for update
            Version = 1
        };

        // Act
        await repository.UpdateAsync(updatedCustomer);

        // Assert - Database update received encrypted fields
        Assert.NotNull(capturedUpdateEntity);
        Assert.NotEqual("TAX-NEW", capturedUpdateEntity.TaxId);
        Assert.NotEqual("SECRET-NEW", capturedUpdateEntity.SecretCode);
        Assert.Equal("TAX-NEW", encryptionProvider.Decrypt(capturedUpdateEntity.TaxId));
        Assert.Equal("SECRET-NEW", encryptionProvider.Decrypt(capturedUpdateEntity.SecretCode));

        // Assert - Audit log contains masked values, NOT plaintext and NOT ciphertext!
        var entries = auditSink.GetEntries();
        Assert.Single(entries);
        var entry = entries[0];

        Assert.Contains(entry.PropertyDiffs, d => d.PropertyName == "TaxId");
        var taxDiff = entry.PropertyDiffs.First(d => d.PropertyName == "TaxId");
        Assert.Equal("*******", taxDiff.OldValue); // "TAX-OLD" is 7 chars
        Assert.Equal("*******", taxDiff.NewValue); // "TAX-NEW" is 7 chars

        Assert.Contains(entry.PropertyDiffs, d => d.PropertyName == "SecretCode");
        var codeDiff = entry.PropertyDiffs.First(d => d.PropertyName == "SecretCode");
        Assert.Equal("**********", codeDiff.OldValue); // "SECRET-OLD" is 10 chars
        Assert.Equal("**********", codeDiff.NewValue); // "SECRET-NEW" is 10 chars
    }

    [Fact]
    public void KmsEnvelopeEncryptionProvider_EncryptsAndDecrypts_UsingKmsDecryptedKey()
    {
        // Arrange
        var kmsClient = new LocalMockKmsClient();
        var rawKey = Convert.ToBase64String(Encoding.UTF8.GetBytes("12345678901234567890123456789012")); // 32 bytes
        var encryptedKey = kmsClient.EncryptKey(rawKey);

        // Act
        var provider = new KmsEnvelopeEncryptionProvider(kmsClient, encryptedKey);
        
        var plainText = "MySuperSecretValue123";
        var cipherText = provider.Encrypt(plainText);
        var decryptedText = provider.Decrypt(cipherText);

        // Assert
        Assert.NotEqual(plainText, cipherText);
        Assert.Equal(plainText, decryptedText);
    }
}
