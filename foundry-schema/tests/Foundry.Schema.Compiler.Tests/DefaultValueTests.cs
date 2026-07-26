using Foundry.Schema.Compiler;
using Xunit;

namespace Foundry.Schema.Compiler.Tests;

public class DefaultValueTests
{
    [Fact]
    public void StringProperty_GetsStringEmptyDefault()
    {
        var entity = TestHelpers.MakeEntityWithProperty("Name", "string");
        var code = TestHelpers.GenerateForSingleEntity(entity);

        Assert.Contains("= string.Empty;", code);
    }

    [Fact]
    public void BoolProperty_GetsFalseDefault()
    {
        var entity = TestHelpers.MakeEntityWithProperty("IsActive", "bool");
        var code = TestHelpers.GenerateForSingleEntity(entity);

        Assert.Contains("= false;", code);
    }

    [Theory]
    [InlineData("int")]
    [InlineData("decimal")]
    [InlineData("double")]
    [InlineData("float")]
    public void NumericProperty_GetsZeroDefault(string schemaType)
    {
        var entity = TestHelpers.MakeEntityWithProperty("Amount", schemaType);
        var code = TestHelpers.GenerateForSingleEntity(entity);

        Assert.Contains("= 0;", code);
    }

    [Fact]
    public void EnumProperty_GetsDefaultOfTypeDefault()
    {
        var entity = TestHelpers.MakeEntityWithProperty("Status", "OrderStatus", isEnum: true);
        var code = TestHelpers.GenerateForSingleEntity(entity);

        Assert.Contains("= default(OrderStatus);", code);
    }

    [Fact]
    public void DateTimeProperty_GetsNoDefault()
    {
        // DateTime is not a string, bool, int, decimal, double, float, or enum —
        // so the code assigns no default value.
        var entity = TestHelpers.MakeEntityWithProperty("CreatedAt", "datetime");
        var code = TestHelpers.GenerateForSingleEntity(entity);

        Assert.Contains("public DateTime CreatedAt { get; init; }", code);
        Assert.DoesNotContain("= default(DateTime);", code);
    }

    [Fact]
    public void ObjectIdProperty_GetsNoDefault()
    {
        var entity = TestHelpers.MakeEntityWithProperty("RefId", "objectid");
        var code = TestHelpers.GenerateForSingleEntity(entity);

        Assert.Contains("public ObjectId RefId { get; init; }", code);
    }

    [Fact]
    public void AllProperties_UseGetInitPattern()
    {
        var entity = TestHelpers.MakeEntityWithProperty("Value", "string");
        var code = TestHelpers.GenerateForSingleEntity(entity);

        Assert.Contains("{ get; init; }", code);
    }

    [Fact]
    public void CustomType_NonEnum_GetsNoDefault()
    {
        // A custom type that is NOT marked as enum should get no default
        var entity = TestHelpers.MakeEntityWithProperty("Address", "AddressInfo");
        var code = TestHelpers.GenerateForSingleEntity(entity);

        Assert.Contains("public AddressInfo Address { get; init; }", code);
        Assert.DoesNotContain("= default(AddressInfo);", code);
    }
}
