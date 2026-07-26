using Foundry.Schema.Compiler;
using Xunit;

namespace Foundry.Schema.Compiler.Tests;

public class EnumGenerationTests
{
    [Fact]
    public void BasicEnum_GeneratesCorrectDeclaration()
    {
        var enumDef = new Foundry.Schema.Compiler.Enum
        {
            Name = "OrderStatus",
            Values = new List<string> { "Pending", "Completed", "Cancelled" }
        };
        var code = TestHelpers.GenerateForSingleEnum(enumDef);

        Assert.Contains("public enum OrderStatus", code);
    }

    [Fact]
    public void BasicEnum_ContainsAllValues()
    {
        var enumDef = new Foundry.Schema.Compiler.Enum
        {
            Name = "OrderStatus",
            Values = new List<string> { "Pending", "Completed", "Cancelled" }
        };
        var code = TestHelpers.GenerateForSingleEnum(enumDef);

        Assert.Contains("Pending", code);
        Assert.Contains("Completed", code);
        Assert.Contains("Cancelled", code);
    }

    [Fact]
    public void EnumNamespace_MatchesSchemaNamespace()
    {
        var enumDef = new Foundry.Schema.Compiler.Enum
        {
            Name = "Color",
            Values = new List<string> { "Red", "Blue" }
        };
        var code = TestHelpers.GenerateForSingleEnum(enumDef, ns: "MyApp.Domain");

        Assert.Contains("namespace MyApp.Domain;", code);
    }

    [Fact]
    public void SingleValueEnum_GeneratesCorrectly()
    {
        var enumDef = new Foundry.Schema.Compiler.Enum
        {
            Name = "SingleStatus",
            Values = new List<string> { "Active" }
        };
        var code = TestHelpers.GenerateForSingleEnum(enumDef);

        Assert.Contains("public enum SingleStatus", code);
        Assert.Contains("Active", code);
    }

    [Fact]
    public void EmptyEnum_GeneratesEnumWithNoValues()
    {
        var enumDef = new Foundry.Schema.Compiler.Enum
        {
            Name = "EmptyEnum",
            Values = new List<string>()
        };
        var code = TestHelpers.GenerateForSingleEnum(enumDef);

        Assert.Contains("public enum EmptyEnum", code);
        Assert.Contains("{", code);
        Assert.Contains("}", code);
    }

    [Fact]
    public void EnumValues_AreSeparatedByCommas()
    {
        var enumDef = new Foundry.Schema.Compiler.Enum
        {
            Name = "Priority",
            Values = new List<string> { "Low", "Medium", "High" }
        };
        var code = TestHelpers.GenerateForSingleEnum(enumDef);

        // Values are joined with ",\n    " so consecutive values appear on separate lines
        Assert.Contains("Low,", code);
        Assert.Contains("Medium,", code);
        // Last value has no trailing comma
        Assert.Contains("High", code);
    }

    [Fact]
    public void EnumOutput_DoesNotContainUsingStatements()
    {
        // Enums should not have using statements (unlike entities)
        var enumDef = new Foundry.Schema.Compiler.Enum
        {
            Name = "Status",
            Values = new List<string> { "Active" }
        };
        var code = TestHelpers.GenerateForSingleEnum(enumDef);

        Assert.DoesNotContain("using System;", code);
        Assert.DoesNotContain("using MongoDB", code);
    }
}
