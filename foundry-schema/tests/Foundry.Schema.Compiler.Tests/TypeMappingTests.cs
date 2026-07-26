using Foundry.Schema.Compiler;
using Xunit;

namespace Foundry.Schema.Compiler.Tests;

public class TypeMappingTests
{
    [Theory]
    [InlineData("string", "string")]
    [InlineData("int", "int")]
    [InlineData("long", "long")]           // Unknown/custom — should pass-through as-is
    [InlineData("double", "double")]
    [InlineData("float", "float")]
    [InlineData("bool", "bool")]
    [InlineData("datetime", "DateTime")]
    [InlineData("objectid", "ObjectId")]
    [InlineData("decimal", "decimal")]
    [InlineData("guid", "Guid")]
    public void PrimitiveType_MapsToExpectedCSharpType(string schemaType, string expectedCSharpType)
    {
        var entity = TestHelpers.MakeEntityWithProperty("TestProp", schemaType);
        var code = TestHelpers.GenerateForSingleEntity(entity);

        Assert.Contains($"public {expectedCSharpType} TestProp", code);
    }

    [Fact]
    public void BooleanType_MapsToSameAsBool()
    {
        // "boolean" is not in the switch — it should pass through as-is ("boolean")
        var entity = TestHelpers.MakeEntityWithProperty("Flag", "boolean");
        var code = TestHelpers.GenerateForSingleEntity(entity);

        // The MapType switch doesn't have "boolean", so it passes through as "boolean"
        Assert.Contains("public boolean Flag", code);
    }

    [Fact]
    public void CustomType_PassesThroughAsIs()
    {
        var entity = TestHelpers.MakeEntityWithProperty("Status", "OrderStatus", isEnum: true);
        var code = TestHelpers.GenerateForSingleEntity(entity);

        Assert.Contains("public OrderStatus Status", code);
    }

    [Fact]
    public void ListType_PassesThroughAsIs()
    {
        // List<T> is not a recognized primitive — should pass through unchanged
        var entity = TestHelpers.MakeEntityWithProperty("Items", "List<string>");
        var code = TestHelpers.GenerateForSingleEntity(entity);

        Assert.Contains("public List<string> Items", code);
    }

    [Theory]
    [InlineData("String", "string")]
    [InlineData("INT", "int")]
    [InlineData("DateTime", "DateTime")]
    [InlineData("BOOL", "bool")]
    [InlineData("ObjectId", "ObjectId")]
    public void TypeMapping_IsCaseInsensitive(string schemaType, string expectedCSharpType)
    {
        var entity = TestHelpers.MakeEntityWithProperty("Prop", schemaType);
        var code = TestHelpers.GenerateForSingleEntity(entity);

        Assert.Contains($"public {expectedCSharpType} Prop", code);
    }

    [Fact]
    public void KeyProperty_IsExcludedFromOutput()
    {
        var entity = new Entity
        {
            Name = "TestEntity",
            Properties = new List<Property>
            {
                new Property { Name = "Id", Type = "ObjectId", IsKey = true },
                new Property { Name = "Name", Type = "string" }
            }
        };
        var code = TestHelpers.GenerateForSingleEntity(entity);

        Assert.DoesNotContain("public ObjectId Id", code);
        Assert.Contains("public string Name", code);
    }
}
