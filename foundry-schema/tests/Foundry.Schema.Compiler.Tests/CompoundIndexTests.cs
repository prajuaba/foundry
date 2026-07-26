using Foundry.Schema.Compiler;
using Xunit;

namespace Foundry.Schema.Compiler.Tests;

/// <summary>
/// Covers emission of entity-level indexes.
/// </summary>
/// <remarks>
/// These declarations were previously parsed and validated but never emitted: nothing in the
/// generator referenced <c>Entity.Indexes</c>. The result was the quietest possible failure — the
/// index appeared in the schema and in Studio, the developer believed it existed, and every query
/// against it did a collection scan. The first test here is the regression guard for that.
/// </remarks>
public class CompoundIndexTests
{
    private static Entity EntityWith(List<Property> properties, List<Index> indexes) => new()
    {
        Name = "Order",
        Properties = properties,
        Indexes = indexes
    };

    private static Property Prop(string name, string type = "string", params string[] attributes) => new()
    {
        Name = name,
        Type = type,
        Attributes = attributes.ToList()
    };

    [Fact]
    public void CompositeIndex_IsEmittedAsCompoundIndexAttribute()
    {
        var entity = EntityWith(
            new List<Property>
            {
                new() { Name = "Id", Type = "ObjectId", IsKey = true },
                Prop("CustomerId", "ObjectId"),
                Prop("OrderDate", "DateTime")
            },
            new List<Index> { new() { Fields = new List<string> { "CustomerId", "OrderDate" } } });

        var code = TestHelpers.GenerateForSingleEntity(entity);

        Assert.Contains("[CompoundIndex(\"CustomerId\", \"OrderDate\")]", code);
    }

    [Fact]
    public void CompositeIndex_PreservesFieldOrder()
    {
        // Field order determines which queries the index can serve, so it must survive emission.
        var entity = EntityWith(
            new List<Property>
            {
                new() { Name = "Id", Type = "ObjectId", IsKey = true },
                Prop("OrderDate", "DateTime"),
                Prop("CustomerId", "ObjectId")
            },
            new List<Index> { new() { Fields = new List<string> { "OrderDate", "CustomerId" } } });

        var code = TestHelpers.GenerateForSingleEntity(entity);

        Assert.Contains("[CompoundIndex(\"OrderDate\", \"CustomerId\")]", code);
    }

    [Fact]
    public void UniqueIndex_EmitsUniqueOption()
    {
        var entity = EntityWith(
            new List<Property>
            {
                new() { Name = "Id", Type = "ObjectId", IsKey = true },
                Prop("TenantId", "ObjectId"),
                Prop("Code")
            },
            new List<Index> { new() { Fields = new List<string> { "TenantId", "Code" }, Unique = true } });

        var code = TestHelpers.GenerateForSingleEntity(entity);

        Assert.Contains("[CompoundIndex(\"TenantId\", \"Code\", Unique = true)]", code);
    }

    [Fact]
    public void NamedIndex_EmitsNameOption()
    {
        var entity = EntityWith(
            new List<Property>
            {
                new() { Name = "Id", Type = "ObjectId", IsKey = true },
                Prop("ScheduledTime", "DateTime")
            },
            new List<Index> { new() { Name = "ByScheduledTime", Fields = new List<string> { "ScheduledTime" } } });

        var code = TestHelpers.GenerateForSingleEntity(entity);

        Assert.Contains("[CompoundIndex(\"ScheduledTime\", Name = \"ByScheduledTime\")]", code);
    }

    /// <summary>
    /// MongoDB rejects two indexes over the same key pattern under different names, so a
    /// single-field entity index that merely restates a property attribute must not be emitted.
    /// </summary>
    [Theory]
    [InlineData("Indexed")]
    [InlineData("Index")]
    [InlineData("Unique")]
    [InlineData("UniqueIndex")]
    public void SingleFieldIndex_IsSkippedWhenPropertyAlreadyCarriesTheAttribute(string attribute)
    {
        var entity = EntityWith(
            new List<Property>
            {
                new() { Name = "Id", Type = "ObjectId", IsKey = true },
                Prop("Email", "string", attribute)
            },
            new List<Index> { new() { Fields = new List<string> { "Email" } } });

        var code = TestHelpers.GenerateForSingleEntity(entity);

        Assert.DoesNotContain("CompoundIndex", code);
    }

    [Fact]
    public void SingleFieldIndex_IsEmittedWhenPropertyHasNoIndexAttribute()
    {
        var entity = EntityWith(
            new List<Property>
            {
                new() { Name = "Id", Type = "ObjectId", IsKey = true },
                Prop("ScheduledTime", "DateTime")
            },
            new List<Index> { new() { Fields = new List<string> { "ScheduledTime" } } });

        var code = TestHelpers.GenerateForSingleEntity(entity);

        Assert.Contains("[CompoundIndex(\"ScheduledTime\")]", code);
    }

    [Fact]
    public void EntityWithoutIndexes_EmitsNoCompoundIndexAttribute()
    {
        var entity = EntityWith(
            new List<Property> { new() { Name = "Id", Type = "ObjectId", IsKey = true } },
            new List<Index>());

        var code = TestHelpers.GenerateForSingleEntity(entity);

        Assert.DoesNotContain("CompoundIndex", code);
    }

    [Fact]
    public void CompoundIndexAttribute_IsImported()
    {
        var entity = EntityWith(
            new List<Property>
            {
                new() { Name = "Id", Type = "ObjectId", IsKey = true },
                Prop("A"),
                Prop("B")
            },
            new List<Index> { new() { Fields = new List<string> { "A", "B" } } });

        var code = TestHelpers.GenerateForSingleEntity(entity);

        // The attribute lives in Foundry.Core.Entities, which the entity template always imports.
        Assert.Contains("using Foundry.Core.Entities;", code);
    }
}
