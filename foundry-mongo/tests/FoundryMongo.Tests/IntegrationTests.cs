using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Foundry.Mongo.DependencyInjection;
using Foundry.Core.Audit;
using Foundry.Core.User;
using Foundry.Core.Entities;
using Foundry.Core.Entities;
using Foundry.Core.Security;
using Foundry.Mongo.Repositories;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace Foundry.Mongo.Tests;

public class IntegrationTests : IDisposable
{
    private readonly string _dbName;
    private readonly MongoClient _client;
    private readonly IMongoDatabase _db;
    private static readonly string EncryptionKey = Convert.ToBase64String(Encoding.UTF8.GetBytes("12345678901234567890123456789012")); // 32 bytes

    public record IntegrationTestCustomer : BaseEntity<ObjectId>, IVersionable, ISoftDelete
    {
        public string Name { get; set; } = string.Empty;

        [SensitiveData(Protection = ProtectionType.Encrypt)]
        public string SecretCode { get; set; } = string.Empty;

        public bool IsDeleted { get; init; }
        public DateTime? DeletedAt { get; init; }
    }

    public IntegrationTests()
    {
        // This test constructs Repository<T> directly rather than going through
        // AddFoundryMongo, which is the only thing that registers the global convention pack.
        // Without this call the test depended on some *other* test's DI setup having run first,
        // so it passed or failed according to xUnit's scheduling: it asserts on the camelCase
        // element name 'secretCode', which only exists once CamelCaseElementNameConvention is
        // registered. Registering here is idempotent and makes the test self-sufficient.
        Foundry.Mongo.Infrastructure.Conventions.MongoDbConventions.Register();

        _dbName = $"FoundryMongo_Integration_{Guid.NewGuid():N}";
        _client = new MongoClient("mongodb://localhost:27017");
        _db = _client.GetDatabase(_dbName);
    }

    public void Dispose()
    {
        // Clean up the integration test database
        try
        {
            _client.DropDatabase(_dbName);
        }
        catch
        {
            // Ignore cleanup failures
        }
    }

    [Fact]
    public async Task FullFlow_WithRealMongoDB()
    {
        // 1. Arrange repository dependencies
        var auditSink = new InMemoryAuditSink();
        var userContext = new AmbientUserContext();
        var encryptionProvider = new AesEncryptionProvider(EncryptionKey);
        var repo = new Repository<IntegrationTestCustomer>(_db, auditSink, userContext, encryptionProvider);

        // Make sure index creation works
        await repo.CreateIndexesAsync();

        var customer = new IntegrationTestCustomer
        {
            Id = ObjectId.GenerateNewId(),
            Name = "John Doe Corp",
            SecretCode = "TopSecret123"
        };

        // 2. Act - Insert
        await repo.InsertAsync(customer);

        // 3. Assert - Read decrypted from repository
        var retrieved = await repo.GetByIdAsync(customer.Id);
        Assert.NotNull(retrieved);
        Assert.Equal("John Doe Corp", retrieved.Name);
        Assert.Equal("TopSecret123", retrieved.SecretCode); // Decrypted successfully
        Assert.Equal(1, retrieved.Version);

        // 4. Assert - Verify raw DB contains encrypted string
        var rawCollection = _db.GetCollection<BsonDocument>("IntegrationTestCustomers");
        var rawDoc = await rawCollection.Find(Builders<BsonDocument>.Filter.Eq("_id", customer.Id)).FirstOrDefaultAsync();
        Assert.NotNull(rawDoc);
        
        var secretCodeValue = rawDoc["secretCode"].AsString;
        Assert.NotEqual("TopSecret123", secretCodeValue); // Must be encrypted in the database
        
        // Decrypted to double-check
        var decryptedSecretCode = encryptionProvider.Decrypt(secretCodeValue);
        Assert.Equal("TopSecret123", decryptedSecretCode);

        // 5. Act - Update
        await repo.UpdateByObjectIdAsync(customer.Id, c =>
        {
            c.Name = "John Doe Corporation";
            c.SecretCode = "NewTopSecret456";
            return c;
        }, operatorId: "admin-user");

        // 6. Assert - Verify updated value
        var updated = await repo.GetByIdAsync(customer.Id);
        Assert.NotNull(updated);
        Assert.Equal("John Doe Corporation", updated.Name);
        Assert.Equal("NewTopSecret456", updated.SecretCode);
        Assert.Equal(2, updated.Version); // Version incremented

        // Verify audit log has 2 entries (Insert, Update)
        var logs = auditSink.GetEntries();
        Assert.Equal(2, logs.Count);
        Assert.Contains(logs, l => l.Action == AuditAction.Inserted);
        Assert.Contains(logs, l => l.Action == AuditAction.Updated);

        var revisions = await repo.GetRevisionsAsync(customer.Id);
        Assert.Equal(2, revisions.Count);
        Assert.Equal(2, revisions[0].Version);
        Assert.Equal(1, revisions[1].Version);

        // 7. Act - Soft delete
        await repo.DeleteAsync(customer.Id);

        // 8. Assert - Soft delete applied
        var deletedRetrieved = await repo.GetByIdAsync(customer.Id);
        Assert.Null(deletedRetrieved); // Repository filters out soft-deleted records by default

        // Verify in raw collection that it still exists but is marked as deleted
        var rawDocAfterDelete = await rawCollection.Find(Builders<BsonDocument>.Filter.Eq("_id", customer.Id)).FirstOrDefaultAsync();
        Assert.NotNull(rawDocAfterDelete);
        Assert.True(rawDocAfterDelete["isDeleted"].AsBoolean);
        Assert.NotNull(rawDocAfterDelete["deletedAt"]);
    }
}
