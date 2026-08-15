using System;
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

/// <summary>
/// A POST returned an id of all zeroes for a record that had in fact been stored under a real one.
/// </summary>
/// <remarks>
/// <para>
/// The MongoDB driver generates the id during <c>InsertOneAsync</c> and stamps it on the instance it
/// was handed. When any property is encrypted or masked, that instance is a clone produced by
/// <c>EncryptEntityForWrite</c> — so the id landed on the copy, and the caller's entity, which is
/// what the endpoint serializes into the response, kept <see cref="ObjectId.Empty"/>.
/// </para>
/// <para>
/// The response body was only the visible half. The revision snapshot and the audit entry are both
/// keyed from the same field, so every audited insert of an encrypted entity was recorded against
/// <c>000000000000000000000000</c> and the audit trail could not tell one record from another.
/// </para>
/// <para>
/// Entities with no encrypted properties were never affected, because <c>EncryptEntityForWrite</c>
/// returns the same reference for them. That is why this survived: the defect was invisible to every
/// entity that did not opt into encryption.
/// </para>
/// </remarks>
public class InsertAssignsIdTests
{
    public record PlainCustomer : BaseEntity<ObjectId>, IVersionable
    {
        public string Name { get; set; } = string.Empty;
    }

    public record EncryptedCustomer : BaseEntity<ObjectId>, IVersionable
    {
        public string Name { get; set; } = string.Empty;

        [SensitiveData(Protection = ProtectionType.Encrypt)]
        public string TaxId { get; set; } = string.Empty;
    }

    private static readonly string EncryptionKey =
        Convert.ToBase64String(Encoding.UTF8.GetBytes("12345678901234567890123456789012"));

    private static (IMongoDatabase Db, IMongoCollection<T> Collection) MockFor<T>()
    {
        var db = Substitute.For<IMongoDatabase>();
        var collection = Substitute.For<IMongoCollection<T>>();
        collection.CollectionNamespace.Returns(
            new CollectionNamespace(new DatabaseNamespace("TestDb"), typeof(T).Name + "s"));
        collection.Database.Returns(db);
        db.GetCollection<T>(Arg.Any<string>()).Returns(collection);
        return (db, collection);
    }

    [Fact]
    public async Task AnEncryptedEntityKeepsTheIdItWasStoredUnder()
    {
        var (db, _) = MockFor<EncryptedCustomer>();
        var repository = new Repository<EncryptedCustomer>(db, null, null, new AesEncryptionProvider(EncryptionKey));

        var customer = new EncryptedCustomer { Id = ObjectId.Empty, Name = "Alice Corp", TaxId = "TAX-999-111" };

        await repository.InsertAsync(customer);

        Assert.NotEqual(ObjectId.Empty, customer.Id);
    }

    [Fact]
    public async Task TheCallerAndTheStoredDocumentAgreeOnTheId()
    {
        // The precise shape of the defect: two instances, one id, and the caller held the wrong one.
        var (db, collection) = MockFor<EncryptedCustomer>();
        var repository = new Repository<EncryptedCustomer>(db, null, null, new AesEncryptionProvider(EncryptionKey));

        EncryptedCustomer? written = null;
        await collection.InsertOneAsync(
            Arg.Do<EncryptedCustomer>(e => written = e),
            Arg.Any<InsertOneOptions>(),
            Arg.Any<CancellationToken>());

        var customer = new EncryptedCustomer { Id = ObjectId.Empty, Name = "Alice Corp", TaxId = "TAX-999-111" };
        await repository.InsertAsync(customer);

        Assert.NotNull(written);
        Assert.Equal(written!.Id, customer.Id);
        Assert.NotEqual(ObjectId.Empty, written.Id);
    }

    [Fact]
    public async Task TheAuditEntryIsKeyedToTheRealId()
    {
        // Audit rows are written from the caller's instance, so they inherited the zeroed id and
        // every insert of an encrypted entity looked like the same record.
        var (db, _) = MockFor<EncryptedCustomer>();
        var auditSink = Substitute.For<IAuditSink>();
        var repository = new Repository<EncryptedCustomer>(
            db, auditSink, null, new AesEncryptionProvider(EncryptionKey));

        var customer = new EncryptedCustomer { Id = ObjectId.Empty, Name = "Alice Corp", TaxId = "TAX-999-111" };
        await repository.InsertAsync(customer);

        await auditSink.Received(1).WriteAsync(
            Arg.Is<AuditLogEntry>(e => e.EntityId != ObjectId.Empty.ToString()),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnEntityWithoutEncryptionStillGetsAnId()
    {
        // The path that always worked, asserted so the fix cannot regress it: for these entities
        // EncryptEntityForWrite returns the same reference and the driver's own stamp was enough.
        var (db, _) = MockFor<PlainCustomer>();
        var repository = new Repository<PlainCustomer>(db, null, null, null);

        var customer = new PlainCustomer { Id = ObjectId.Empty, Name = "Bob Ltd" };
        await repository.InsertAsync(customer);

        Assert.NotEqual(ObjectId.Empty, customer.Id);
    }

    [Fact]
    public async Task AnIdTheCallerChoseIsNotOverwritten()
    {
        // Assigning the id before the clone is taken must not take the choice away from a caller
        // who supplied one -- inserting with a known id is how fixtures and migrations work.
        var (db, _) = MockFor<EncryptedCustomer>();
        var repository = new Repository<EncryptedCustomer>(db, null, null, new AesEncryptionProvider(EncryptionKey));

        var chosen = ObjectId.GenerateNewId();
        var customer = new EncryptedCustomer { Id = chosen, Name = "Alice Corp", TaxId = "TAX-1" };

        await repository.InsertAsync(customer);

        Assert.Equal(chosen, customer.Id);
    }
}
