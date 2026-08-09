using Foundry.Schema.Compiler;
using Xunit;

namespace Foundry.Schema.Compiler.Tests;

/// <summary>
/// Encryption and masking are alternatives, and asking for both used to pick one in silence.
/// </summary>
public class EncryptAndMaskExclusivityTests
{


    private static SchemaModel SchemaWithProtection(params string[] attributes)
        => new()
        {
            Namespace = "Protection.Test",
            Entities = new List<Entity>
            {
                new()
                {
                    Name = "Client",
                    Properties = new List<Property>
                    {
                        new() { Name = "Id", Type = "ObjectId", IsKey = true },
                        new() { Name = "Email", Type = "string", Attributes = new List<string>(attributes) },
                    },
                },
            },
        };

    [Theory]
    [InlineData("Mask")]
    [InlineData("MaskEmail")]
    public void DeclaringBothEncryptionAndMaskingIsAnError(string mask)
    {
        // ProtectionType is a single value, so the compiler emitted Encrypt and dropped the mask
        // without a word -- the value then comes back in full to everyone entitled to read the
        // entity, which is the opposite of what asking for a mask means. The showcase's own
        // Customer.Email declared exactly this pair.
        var result = SchemaValidator.Validate(SchemaWithProtection("Encrypt", mask));

        var error = Assert.Single(
            result.Items, d => d.Code == DiagnosticCatalog.EncryptAndMaskOnOneProperty);
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.Contains(mask, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Encrypt")]
    [InlineData("Mask")]
    [InlineData("MaskEmail")]
    public void EitherOneAloneIsFine(string attribute)
    {
        var result = SchemaValidator.Validate(SchemaWithProtection(attribute));

        Assert.DoesNotContain(
            result.Items, d => d.Code == DiagnosticCatalog.EncryptAndMaskOnOneProperty);
    }
}
