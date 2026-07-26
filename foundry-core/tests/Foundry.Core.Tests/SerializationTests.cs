using System.Text.Json;
using Foundry.Core.Entities;
using Foundry.Core.Serialization;
using MongoDB.Bson;
using Xunit;

namespace Foundry.Core.Tests;

/// <summary>
/// The wire contract produced by <see cref="FoundryJsonDefaults"/>.
/// </summary>
/// <remarks>
/// Every HTTP request and response in a generated application passes through these options, so a
/// regression here is a regression in every endpoint at once. Three of the properties asserted
/// below were live production-breaking defects: ObjectId did not round-trip (a POSTed entity was
/// stored under an id the caller never saw), the required <c>Id</c> made every POST fail model
/// binding, and audit timestamps were writable by clients. The fourth -- that <c>Version</c> is
/// deliberately <em>not</em> read-only -- is asserted because "hide the server-owned fields" is the
/// obvious-looking change that would silently break optimistic concurrency.
/// </remarks>
public class SerializationTests
{
    private enum Grade
    {
        Standard,
        Premium
    }

    private sealed record Customer : BaseEntity<ObjectId>, ISoftDelete
    {
        public string Name { get; init; } = string.Empty;
        public Grade Grade { get; init; }
        public bool IsDeleted { get; init; }
        public DateTime? DeletedAt { get; init; }
    }

    private static JsonSerializerOptions Options() => FoundryJsonDefaults.CreateOptions();

    // ---- ObjectId ----

    [Fact]
    public void ObjectId_RoundTripsThroughItsHexString()
    {
        // Without a converter, System.Text.Json serialises ObjectId's members and decodes back to
        // ObjectId.Empty. The driver then treats an empty id as unset and assigns a different one
        // at insert, so a GET by the id the caller was handed returned 404 while the record sat in
        // the collection.
        var id = ObjectId.GenerateNewId();
        var customer = new Customer { Id = id, Name = "Ada" };

        var json = JsonSerializer.Serialize(customer, Options());
        var restored = JsonSerializer.Deserialize<Customer>(json, Options());

        Assert.Contains(id.ToString(), json);
        Assert.NotNull(restored);
        Assert.Equal(id, restored!.Id);
        Assert.NotEqual(ObjectId.Empty, restored.Id);
    }

    [Fact]
    public void ObjectId_IsWrittenAsAStringNotAnObject()
    {
        var customer = new Customer { Id = ObjectId.GenerateNewId(), Name = "Ada" };

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(customer, Options()));
        var idProperty = document.RootElement.EnumerateObject()
            .First(p => p.Name.Equals("Id", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(JsonValueKind.String, idProperty.Value.ValueKind);
    }

    [Fact]
    public void MalformedObjectId_IsRejectedRatherThanSilentlyBecomingEmpty()
    {
        // Decoding a bad id to ObjectId.Empty would let the driver assign a fresh one, turning a
        // malformed request into a successful write against the wrong document.
        var json = """{"Id":"not-an-object-id","Name":"Ada"}""";

        Assert.ThrowsAny<Exception>(() => JsonSerializer.Deserialize<Customer>(json, Options()));
    }

    // ---- required Id on the wire ----

    [Fact]
    public void Id_MayBeOmittedOnDeserialization()
    {
        // BaseEntity.Id is `required`, which is correct for C# but which STJ enforces on the wire.
        // A client cannot supply an id because the server assigns it, so POST failed model binding
        // with a bodyless 400 before the handler ran.
        var restored = JsonSerializer.Deserialize<Customer>("""{"Name":"Ada"}""", Options());

        Assert.NotNull(restored);
        Assert.Equal("Ada", restored!.Name);
        Assert.Equal(ObjectId.Empty, restored.Id);
    }

    // ---- server-owned fields ----

    [Fact]
    public void AuditTimestamps_AreEmittedButIgnoredOnInput()
    {
        var clientSupplied = """
        {"Name":"Ada","CreatedAtUtc":"2000-01-01T00:00:00Z","UpdatedAtUtc":"2000-01-01T00:00:00Z"}
        """;

        var restored = JsonSerializer.Deserialize<Customer>(clientSupplied, Options())!;

        Assert.NotEqual(new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc), restored.CreatedAtUtc);
        Assert.NotEqual(new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc), restored.UpdatedAtUtc);

        // Still readable by clients -- they are suppressed on the way in, not hidden.
        Assert.Contains("CreatedAtUtc", JsonSerializer.Serialize(restored, Options()));
    }

    [Fact]
    public void Version_RoundTripsBecauseItIsTheConcurrencyToken()
    {
        // Deliberately NOT server-owned. The repository reads Version from the incoming entity and
        // filters the update on it, so a client must send it back for a conflicting write to be
        // detected. Making it read-only would bind it to 0 and every update would filter on a
        // version no document has -- optimistic concurrency would silently stop working.
        var restored = JsonSerializer.Deserialize<Customer>("""{"Name":"Ada","Version":7}""", Options())!;

        Assert.Equal(7, restored.Version);
    }

    // ---- enums ----

    [Fact]
    public void Enums_AreWrittenAsNames()
    {
        var json = JsonSerializer.Serialize(new Customer { Id = ObjectId.Empty, Grade = Grade.Premium }, Options());

        Assert.Contains("Premium", json);
        Assert.DoesNotContain("\"Grade\":1", json);
    }

    [Fact]
    public void Enums_AreAcceptedByNameCaseInsensitively()
    {
        var restored = JsonSerializer.Deserialize<Customer>("""{"Name":"Ada","Grade":"premium"}""", Options())!;
        Assert.Equal(Grade.Premium, restored.Grade);
    }

    // ---- Apply() semantics ----

    [Fact]
    public void Apply_IsIdempotent()
    {
        // Hosts may call Apply more than once (ConfigureHttpJsonOptions plus a manual call).
        // Adding a second ObjectIdJsonConverter would be harmless, but a duplicated
        // JsonStringEnumConverter is exactly the kind of thing that changes behaviour subtly.
        var options = new JsonSerializerOptions();

        FoundryJsonDefaults.Apply(options);
        FoundryJsonDefaults.Apply(options);

        Assert.Single(options.Converters.Where(c => c is ObjectIdJsonConverter));
    }

    [Fact]
    public void Apply_OnNullOptions_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => FoundryJsonDefaults.Apply(null!));
    }

    [Fact]
    public void Apply_PreservesConvertersTheHostAlreadyRegistered()
    {
        var options = new JsonSerializerOptions();
        var hostConverter = new System.Text.Json.Serialization.JsonStringEnumConverter();
        options.Converters.Add(hostConverter);

        FoundryJsonDefaults.Apply(options);

        Assert.Contains(hostConverter, options.Converters);
    }
}
