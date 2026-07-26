using Foundry.Schema.Compiler;
using Xunit;

namespace Foundry.Schema.Compiler.Tests;

public class IndexGenerationTests
{
    [Fact]
    public void PropertyWithIndexAttribute_GeneratesIndexedAttribute()
    {
        var entity = new Entity
        {
            Name = "TestEntity",
            Properties = new List<Property>
            {
                new Property
                {
                    Name = "CustomerId",
                    Type = "string",
                    Attributes = new List<string> { "Index" }
                }
            }
        };
        var code = TestHelpers.GenerateForSingleEntity(entity);

        Assert.Contains("[Indexed]", code);
    }

    [Fact]
    public void PropertyWithUniqueIndexAttribute_GeneratesIndexedUniqueAttribute()
    {
        var entity = new Entity
        {
            Name = "TestEntity",
            Properties = new List<Property>
            {
                new Property
                {
                    Name = "Email",
                    Type = "string",
                    Attributes = new List<string> { "UniqueIndex" }
                }
            }
        };
        var code = TestHelpers.GenerateForSingleEntity(entity);

        Assert.Contains("[Indexed(Unique = true)]", code);
    }

    [Fact]
    public void MultiplePropertiesWithDifferentIndexTypes_GenerateCorrectAttributes()
    {
        var entity = new Entity
        {
            Name = "TestEntity",
            Properties = new List<Property>
            {
                new Property
                {
                    Name = "CustomerId",
                    Type = "string",
                    Attributes = new List<string> { "Index" }
                },
                new Property
                {
                    Name = "Email",
                    Type = "string",
                    Attributes = new List<string> { "UniqueIndex" }
                },
                new Property
                {
                    Name = "Description",
                    Type = "string",
                    Attributes = new List<string> { "TextIndex" }
                }
            }
        };
        var code = TestHelpers.GenerateForSingleEntity(entity);

        // All three index types should appear
        Assert.Contains("[Indexed]", code);
        Assert.Contains("[Indexed(Unique = true)]", code);
        Assert.Contains("[TextIndexed]", code);
    }

    [Fact]
    public void EntityWithIndexesArray_AcceptsCompositeIndexDefinition()
    {
        // The Indexes array on Entity is accepted by the model, even if the generator
        // doesn't currently emit composite index comments/attributes.
        var entity = new Entity
        {
            Name = "TestEntity",
            Indexes = new List<Index>
            {
                new Index
                {
                    Fields = new List<string> { "CustomerId", "OrderDate" },
                    Unique = false
                }
            },
            Properties = new List<Property>
            {
                new Property { Name = "CustomerId", Type = "string" },
                new Property { Name = "OrderDate", Type = "datetime" }
            }
        };

        // Should not throw — the model accepts indexes even if they don't appear in output
        var code = TestHelpers.GenerateForSingleEntity(entity);
        Assert.NotEmpty(code);
    }

    [Fact]
    public void EntityWithUniqueCompositeIndex_AcceptsUniqueFlag()
    {
        var entity = new Entity
        {
            Name = "TestEntity",
            Indexes = new List<Index>
            {
                new Index
                {
                    Fields = new List<string> { "TenantId", "Email" },
                    Unique = true
                }
            },
            Properties = new List<Property>
            {
                new Property { Name = "TenantId", Type = "string" },
                new Property { Name = "Email", Type = "string" }
            }
        };

        var code = TestHelpers.GenerateForSingleEntity(entity);
        Assert.NotEmpty(code);
        Assert.Contains("public string TenantId", code);
        Assert.Contains("public string Email", code);
    }

    [Fact]
    public void IndexAndTextIndex_OnSameProperty_BothGenerated()
    {
        var entity = new Entity
        {
            Name = "TestEntity",
            Properties = new List<Property>
            {
                new Property
                {
                    Name = "Title",
                    Type = "string",
                    Attributes = new List<string> { "Index", "TextIndex" }
                }
            }
        };
        var code = TestHelpers.GenerateForSingleEntity(entity);

        Assert.Contains("[Indexed]", code);
        Assert.Contains("[TextIndexed]", code);
    }

    [Fact]
    public void UniqueIndexAndTextIndex_OnSameProperty_BothGenerated()
    {
        var entity = new Entity
        {
            Name = "TestEntity",
            Properties = new List<Property>
            {
                new Property
                {
                    Name = "Code",
                    Type = "string",
                    Attributes = new List<string> { "UniqueIndex", "TextIndex" }
                }
            }
        };
        var code = TestHelpers.GenerateForSingleEntity(entity);

        Assert.Contains("[Indexed(Unique = true)]", code);
        Assert.Contains("[TextIndexed]", code);
    }

    [Fact]
    public void PropertyWithNoIndexAttributes_HasNoIndexDecorations()
    {
        var entity = TestHelpers.MakeEntityWithProperty("Name", "string");
        var code = TestHelpers.GenerateForSingleEntity(entity);

        Assert.DoesNotContain("[Indexed]", code);
        Assert.DoesNotContain("[TextIndexed]", code);
    }
}
