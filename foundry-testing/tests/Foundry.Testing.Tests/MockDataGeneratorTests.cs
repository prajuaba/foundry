using Foundry.Schema.Compiler;
using Foundry.Testing.Generators;
using Xunit;

namespace Foundry.Testing.Tests;

/// <summary>
/// Schema-driven mock data used to populate generated test suites.
/// </summary>
/// <remarks>
/// Mock data that violates the schema it was generated from produces test suites that fail against a
/// correctly-working API. The developer then debugs their application because the generated tests are
/// red, which is the most expensive possible way for this to be wrong.
/// </remarks>
public class MockDataGeneratorTests
{
    private static Entity EntityWith(params Property[] properties) =>
        new() { Name = "Customer", Properties = [.. properties] };

    private static Property Prop(string name, string type, bool isKey = false, params string[] attributes) =>
        new() { Name = name, Type = type, IsKey = isKey, Attributes = [.. attributes] };

    // ---- keys ----

    [Fact]
    public void TheKeyIsAValidObjectId()
    {
        // The MongoDB data layer only supports ObjectId keys, and an ObjectId is 24 hex characters.
        // This produced Guid.NewGuid().ToString("N"), which is 32 — so the mock key could never be
        // parsed, and every generated test that posted or fetched by id was doomed before it ran.
        var data = MockDataGenerator.GenerateEntityMockData(
            EntityWith(Prop("Id", "ObjectId", isKey: true)));

        var id = Assert.IsType<string>(data["Id"]);
        Assert.True(
            MongoDB.Bson.ObjectId.TryParse(id, out _),
            $"generated key '{id}' ({id.Length} chars) is not a valid ObjectId");
    }

    [Fact]
    public void ANonKeyObjectIdReferenceIsAlsoValid()
    {
        // A foreign-key style property, e.g. CustomerId on Order. This fell through to the catch-all
        // arm and produced the literal "test_data".
        var data = MockDataGenerator.GenerateEntityMockData(
            EntityWith(Prop("Id", "ObjectId", isKey: true), Prop("CustomerId", "ObjectId")));

        var reference = Assert.IsType<string>(data["CustomerId"]);
        Assert.True(MongoDB.Bson.ObjectId.TryParse(reference, out _), $"'{reference}' is not a valid ObjectId");
    }

    [Fact]
    public void AGuidPropertyGetsAParsableGuid()
    {
        var data = MockDataGenerator.GenerateEntityMockData(
            EntityWith(Prop("Id", "ObjectId", isKey: true), Prop("ExternalRef", "Guid")));

        Assert.True(Guid.TryParse(Assert.IsType<string>(data["ExternalRef"]), out _));
    }

    // ---- tenancy ----

    [Fact]
    public void TheTenantKeyGetsAStableTenantValue()
    {
        var data = MockDataGenerator.GenerateEntityMockData(
            EntityWith(Prop("Id", "ObjectId", isKey: true), Prop("TenantId", "string", false, "TenantKey")));

        Assert.Equal("tenant-test-1", data["TenantId"]);
    }

    // ---- scalar types ----

    [Fact]
    public void ScalarTypesGetValuesOfTheRightShape()
    {
        var data = MockDataGenerator.GenerateEntityMockData(EntityWith(
            Prop("Id", "ObjectId", isKey: true),
            Prop("Title", "string"),
            Prop("Count", "int"),
            Prop("Amount", "decimal"),
            Prop("Active", "bool"),
            Prop("When", "datetime")));

        Assert.IsType<string>(data["Title"]);
        Assert.IsType<int>(data["Count"]);
        Assert.IsType<decimal>(data["Amount"]);
        Assert.IsType<bool>(data["Active"]);
        Assert.True(DateTime.TryParse(Assert.IsType<string>(data["When"]), out _));
    }

    [Fact]
    public void EveryPropertyIsPopulated()
    {
        var entity = EntityWith(
            Prop("Id", "ObjectId", isKey: true),
            Prop("Title", "string"),
            Prop("Count", "int"));

        var data = MockDataGenerator.GenerateEntityMockData(entity);

        Assert.Equal(3, data.Count);
        Assert.All(data.Values, value => Assert.NotNull(value));
    }

    // ---- schema constraints ----

    [Fact]
    public void AMaxLengthConstraintIsRespected()
    {
        // The generated entity carries [MaxLength(5)], so a 12-character "sample_value" fails the
        // API's own validation. The generated test then reports a 400 as an application defect.
        var data = MockDataGenerator.GenerateEntityMockData(
            EntityWith(Prop("Id", "ObjectId", isKey: true), Prop("Code", "string", false, "MaxLength(5)")));

        var code = Assert.IsType<string>(data["Code"]);
        Assert.True(code.Length <= 5, $"generated value '{code}' exceeds MaxLength(5)");
    }

    [Fact]
    public void ARangeConstraintIsRespected()
    {
        var data = MockDataGenerator.GenerateEntityMockData(
            EntityWith(Prop("Id", "ObjectId", isKey: true), Prop("Rating", "int", false, "Range(1,5)")));

        var rating = Assert.IsType<int>(data["Rating"]);
        Assert.InRange(rating, 1, 5);
    }

    [Fact]
    public void ARequiredStringIsNotEmpty()
    {
        var data = MockDataGenerator.GenerateEntityMockData(
            EntityWith(Prop("Id", "ObjectId", isKey: true), Prop("Name", "string", false, "Required")));

        Assert.False(string.IsNullOrWhiteSpace(Assert.IsType<string>(data["Name"])));
    }

    // ---- PII ----

    [Fact]
    public void AnEmailPropertyGetsSomethingEmailShaped()
    {
        var data = MockDataGenerator.GenerateEntityMockData(
            EntityWith(Prop("Id", "ObjectId", isKey: true), Prop("Email", "string", false, "MaskEmail")));

        Assert.Contains("@", Assert.IsType<string>(data["Email"]));
    }

    [Fact]
    public void ACreditCardPropertyGetsACardShapedValue()
    {
        var data = MockDataGenerator.GenerateEntityMockData(
            EntityWith(Prop("Id", "ObjectId", isKey: true), Prop("Card", "string", false, "PiiCreditCard")));

        Assert.Matches(@"^[\d-]+$", Assert.IsType<string>(data["Card"]));
    }

    // ---- concurrency ----

    [Fact]
    public async Task GeneratingFromManyThreadsProducesUsableData()
    {
        // The generator held a shared Random instance, whose instance methods are not thread-safe:
        // concurrent use can corrupt its internal state so that it returns 0 indefinitely. Suite
        // generation across entities is a natural place to parallelise.
        var entity = EntityWith(
            Prop("Id", "ObjectId", isKey: true),
            Prop("Count", "int"),
            Prop("Amount", "decimal"));

        var results = await Task.WhenAll(Enumerable.Range(0, 200).Select(_ =>
            Task.Run(() => MockDataGenerator.GenerateEntityMockData(entity))));

        Assert.All(results, data =>
        {
            Assert.True(MongoDB.Bson.ObjectId.TryParse((string)data["Id"]!, out _));
            Assert.IsType<int>(data["Count"]);
        });

        // Distinct keys prove the randomness did not collapse.
        Assert.True(
            results.Select(r => (string)r["Id"]!).Distinct().Count() > 190,
            "generated keys were not distinct — the shared Random likely degraded under concurrency");
    }

    [Fact]
    public void AnEntityWithNoPropertiesYieldsNoData()
    {
        Assert.Empty(MockDataGenerator.GenerateEntityMockData(EntityWith()));
    }
}
