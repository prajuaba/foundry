using Xunit;
using Foundry.Schema.Compiler;

namespace Foundry.Schema.Compiler.Tests;

public class DtoGenerationTests
{
    [Fact]
    public void Generate_SimpleDto_GeneratesRecordCorrectly()
    {
        // Arrange
        var dto = new DtoModel
        {
            Name = "OrderCheckoutRequest",
            Properties = new List<DtoProperty>
            {
                new DtoProperty { Name = "CustomerEmail", Type = "string", IsRequired = true, Attributes = new List<string> { "Email", "Required" } },
                new DtoProperty { Name = "TotalAmount", Type = "decimal", IsRequired = true },
                new DtoProperty { Name = "PromoCode", Type = "string", IsRequired = false }
            }
        };

        // Act
        var code = TestHelpers.GenerateForSingleDto(dto, "Ordering.Contracts");

        // Assert
        Assert.Contains("namespace Ordering.Contracts;", code);
        Assert.Contains("public partial record OrderCheckoutRequest", code);
        Assert.Contains("[EmailAddressWhenPresent]", code);
        Assert.Contains("[Required]", code);
        Assert.Contains("public required string CustomerEmail { get; init; } = string.Empty;", code);
        Assert.Contains("public required decimal TotalAmount { get; init; } = 0;", code);
        Assert.Contains("public string PromoCode { get; init; } = string.Empty;", code);
    }

    [Fact]
    public void Generate_DtoWithRangeAndLengthValidations_GeneratesDataAnnotations()
    {
        // Arrange
        var dto = new DtoModel
        {
            Name = "ProductDto",
            Properties = new List<DtoProperty>
            {
                new DtoProperty
                {
                    Name = "Sku",
                    Type = "string",
                    IsRequired = true,
                    Attributes = new List<string> { "MinLength(5)", "MaxLength(20)" }
                },
                new DtoProperty
                {
                    Name = "Price",
                    Type = "double",
                    IsRequired = true,
                    Attributes = new List<string> { "Range(0.01, 999.99)" }
                }
            }
        };

        // Act
        var code = TestHelpers.GenerateForSingleDto(dto, "Inventory.Contracts");

        // Assert
        Assert.Contains("[MinLength(5)]", code);
        Assert.Contains("[MaxLength(20)]", code);
        Assert.Contains("[Range(0.01, 999.99)]", code);
        Assert.Contains("public required string Sku { get; init; } = string.Empty;", code);
        Assert.Contains("public required double Price { get; init; } = 0;", code);
    }
}
