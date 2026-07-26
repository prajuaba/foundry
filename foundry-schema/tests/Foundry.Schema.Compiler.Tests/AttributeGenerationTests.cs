using Foundry.Schema.Compiler;
using Xunit;

namespace Foundry.Schema.Compiler.Tests;

public class AttributeGenerationTests
{
    [Fact]
    public void IndexAttribute_GeneratesIndexedAttribute()
    {
        var entity = TestHelpers.MakeEntityWithProperty("CustomerId", "string",
            attributes: new List<string> { "Index" });
        var code = TestHelpers.GenerateForSingleEntity(entity);

        Assert.Contains("[Indexed]", code);
        Assert.DoesNotContain("[Indexed(Unique = true)]", code);
    }

    [Fact]
    public void UniqueIndexAttribute_GeneratesIndexedUniqueAttribute()
    {
        var entity = TestHelpers.MakeEntityWithProperty("Email", "string",
            attributes: new List<string> { "UniqueIndex" });
        var code = TestHelpers.GenerateForSingleEntity(entity);

        Assert.Contains("[Indexed(Unique = true)]", code);
    }

    [Fact]
    public void UniqueAttribute_AlsoGeneratesIndexedUniqueAttribute()
    {
        var entity = TestHelpers.MakeEntityWithProperty("Code", "string",
            attributes: new List<string> { "Unique" });
        var code = TestHelpers.GenerateForSingleEntity(entity);

        Assert.Contains("[Indexed(Unique = true)]", code);
    }

    [Fact]
    public void TextIndexAttribute_GeneratesTextIndexedAttribute()
    {
        var entity = TestHelpers.MakeEntityWithProperty("Description", "string",
            attributes: new List<string> { "TextIndex" });
        var code = TestHelpers.GenerateForSingleEntity(entity);

        Assert.Contains("[TextIndexed]", code);
    }

    [Fact]
    public void RequiredAttribute_GeneratesRequiredKeyword()
    {
        var entity = TestHelpers.MakeEntityWithProperty("OrderNumber", "string",
            attributes: new List<string> { "Required" });
        var code = TestHelpers.GenerateForSingleEntity(entity);

        Assert.Contains("public required string OrderNumber", code);
    }

    [Fact]
    public void EncryptAttribute_GeneratesSensitiveDataEncrypt()
    {
        var entity = TestHelpers.MakeEntityWithProperty("Token", "string",
            attributes: new List<string> { "Encrypt" });
        var code = TestHelpers.GenerateForSingleEntity(entity);

        Assert.Contains("[SensitiveData(Protection = ProtectionType.Encrypt)]", code);
    }

    [Fact]
    public void MaskAttribute_GeneratesSensitiveDataMask()
    {
        var entity = TestHelpers.MakeEntityWithProperty("SSN", "string",
            attributes: new List<string> { "Mask" });
        var code = TestHelpers.GenerateForSingleEntity(entity);

        Assert.Contains("[SensitiveData(Protection = ProtectionType.Mask)]", code);
    }

    [Fact]
    public void MaskEmailAttribute_GeneratesSensitiveDataMaskEmail()
    {
        var entity = TestHelpers.MakeEntityWithProperty("Email", "string",
            attributes: new List<string> { "MaskEmail" });
        var code = TestHelpers.GenerateForSingleEntity(entity);

        Assert.Contains("[SensitiveData(Protection = ProtectionType.Mask, MaskingType = MaskingType.Email)]", code);
    }

    [Fact]
    public void MultipleAttributes_AllGenerated()
    {
        var entity = TestHelpers.MakeEntityWithProperty("OrderNumber", "string",
            attributes: new List<string> { "Required", "UniqueIndex", "TextIndex" });
        var code = TestHelpers.GenerateForSingleEntity(entity);

        Assert.Contains("[Indexed(Unique = true)]", code);
        Assert.Contains("[TextIndexed]", code);
        Assert.Contains("public required string OrderNumber", code);
    }

    [Fact]
    public void NoAttributes_NoDecorationsGenerated()
    {
        var entity = TestHelpers.MakeEntityWithProperty("Name", "string");
        var code = TestHelpers.GenerateForSingleEntity(entity);

        Assert.DoesNotContain("[Indexed]", code);
        Assert.DoesNotContain("[Indexed(Unique = true)]", code);
        Assert.DoesNotContain("[TextIndexed]", code);
        Assert.DoesNotContain("[SensitiveData", code);
        Assert.DoesNotContain("required", code);
    }

    [Fact]
    public void CustomValidationAttributes_GenerateStandardDataAnnotations()
    {
        var entity = TestHelpers.MakeEntityWithProperty("Username", "string",
            attributes: new List<string> { 
                "MinLength(3)", 
                "MaxLength(20)", 
                "Range(1, 10)", 
                "Regex(\"^[a-zA-Z]+$\")", 
                "Email", 
                "Url", 
                "Phone" 
            });
        var code = TestHelpers.GenerateForSingleEntity(entity);

        Assert.Contains("using System.ComponentModel.DataAnnotations;", code);
        Assert.Contains("[MinLength(3)]", code);
        Assert.Contains("[MaxLength(20)]", code);
        Assert.Contains("[Range(1, 10)]", code);
        Assert.Contains("[RegularExpression(\"^[a-zA-Z]+$\")]", code);
        Assert.Contains("[EmailAddress]", code);
        Assert.Contains("[Url]", code);
        Assert.Contains("[Phone]", code);
    }
}
