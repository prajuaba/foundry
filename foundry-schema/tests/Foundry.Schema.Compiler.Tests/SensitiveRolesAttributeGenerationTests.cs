using Foundry.Schema.Compiler;
using Xunit;

namespace Foundry.Schema.Compiler.Tests;

public class SensitiveRolesAttributeGenerationTests
{
    [Fact]
    public void MaskWithRoles_EmitsRolesArrayOnAttribute()
    {
        var entity = new Entity
        {
            Name = "TestEntity",
            Properties = [
                new Property
                {
                    Name = "SSN",
                    Type = "string",
                    Attributes = ["Mask"],
                    SensitiveCategory = "pii",
                    SensitiveRoles = ["Admin", "Auditor"]
                }
            ]
        };
        var code = TestHelpers.GenerateForSingleEntity(entity);

        Assert.Contains("[SensitiveData(Protection = ProtectionType.Mask, Category = \"pii\", Roles = new[] { \"Admin\", \"Auditor\" })]", code);
    }

    [Fact]
    public void MaskWithoutRoles_AttributeUnchanged()
    {
        var entity = new Entity
        {
            Name = "TestEntity",
            Properties = [
                new Property
                {
                    Name = "SSN",
                    Type = "string",
                    Attributes = ["Mask"],
                    SensitiveCategory = "pii"
                }
            ]
        };
        var code = TestHelpers.GenerateForSingleEntity(entity);

        Assert.Contains("[SensitiveData(Protection = ProtectionType.Mask, Category = \"pii\")]", code);
        Assert.DoesNotContain("Roles =", code);
    }

    [Fact]
    public void EncryptWithRoles_RolesAreNeverEmitted()
    {
        var entity = new Entity
        {
            Name = "TestEntity",
            Properties = [
                new Property
                {
                    Name = "Token",
                    Type = "string",
                    Attributes = ["Encrypt"],
                    SensitiveRoles = ["Admin"]
                }
            ]
        };
        var code = TestHelpers.GenerateForSingleEntity(entity);

        Assert.Contains("[SensitiveData(Protection = ProtectionType.Encrypt)]", code);
        Assert.DoesNotContain("Roles =", code);
    }

    [Fact]
    public void MaskEmailWithRoles_EmitsRolesAfterMaskingType()
    {
        var entity = new Entity
        {
            Name = "TestEntity",
            Properties = [
                new Property
                {
                    Name = "Email",
                    Type = "string",
                    Attributes = ["MaskEmail"],
                    SensitiveRoles = ["Support"]
                }
            ]
        };
        var code = TestHelpers.GenerateForSingleEntity(entity);

        Assert.Contains("[SensitiveData(Protection = ProtectionType.Mask, MaskingType = MaskingType.Email, Roles = new[] { \"Support\" })]", code);
    }

    [Fact]
    public void MaskWithStateProperty_EmitsStatePropertyOnAttribute()
    {
        var entity = new Entity
        {
            Name = "TestEntity",
            Properties = [
                new Property
                {
                    Name = "Amount",
                    Type = "decimal",
                    Attributes = ["Mask"],
                    SensitiveStateProperty = "AmountMaskedState"
                }
            ]
        };
        var code = TestHelpers.GenerateForSingleEntity(entity);

        Assert.Contains("[SensitiveData(Protection = ProtectionType.Mask, StateProperty = \"AmountMaskedState\")]", code);
    }

    [Fact]
    public void MaskWithRolesAndStateProperty_EmitsBothInOrder()
    {
        var entity = new Entity
        {
            Name = "TestEntity",
            Properties = [
                new Property
                {
                    Name = "Amount",
                    Type = "decimal",
                    Attributes = ["Mask"],
                    SensitiveCategory = "financial",
                    SensitiveRoles = ["Admin"],
                    SensitiveStateProperty = "AmountMaskedState"
                }
            ]
        };
        var code = TestHelpers.GenerateForSingleEntity(entity);

        Assert.Contains("[SensitiveData(Protection = ProtectionType.Mask, Category = \"financial\", Roles = new[] { \"Admin\" }, StateProperty = \"AmountMaskedState\")]", code);
    }
}
