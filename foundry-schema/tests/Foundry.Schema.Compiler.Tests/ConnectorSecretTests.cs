using Foundry.Schema.Compiler;
using Xunit;

namespace Foundry.Schema.Compiler.Tests;

/// <summary>
/// A connector credential must name where the secret lives, not carry it.
/// </summary>
/// <remarks>
/// The IR is committed to source control, opened in Studio, and passed to a local model as prompt
/// context by <c>foundry ai</c>. A literal key written here reaches all three, and none of them look
/// like a mistake while it is happening.
/// </remarks>
public class ConnectorSecretTests
{
    private static SchemaModel WithConnector(ConnectorModel connector) => new()
    {
        Namespace = "Test.Domain",
        Entities =
        [
            new Entity
            {
                Name = "Customer",
                Properties = [new Property { Name = "Id", Type = "ObjectId", IsKey = true }]
            }
        ],
        Connectors = [connector]
    };

    [Theory]
    [InlineData("sk_live_1234567890")]
    [InlineData("hunter2")]
    [InlineData("Bearer abcdef")]
    public void ALiteralSecretIsRejected(string literal)
    {
        var bag = SchemaValidator.Validate(WithConnector(new ConnectorModel
        {
            Name = "Payments",
            BaseUrl = "https://payments.example.com",
            Token = literal
        }));

        Assert.Contains(bag.Items, d => d.Code == DiagnosticCatalog.ConnectorSecretLiteral);
        Assert.True(bag.HasErrors);
    }

    [Fact]
    public void AReferenceIsAccepted()
    {
        var bag = SchemaValidator.Validate(WithConnector(new ConnectorModel
        {
            Name = "Payments",
            BaseUrl = "https://payments.example.com",
            Token = "${PAYMENT_GATEWAY_TOKEN}"
        }));

        Assert.DoesNotContain(bag.Items, d => d.Code == DiagnosticCatalog.ConnectorSecretLiteral);
    }

    [Fact]
    public void EveryCredentialFieldIsChecked()
    {
        var bag = SchemaValidator.Validate(WithConnector(new ConnectorModel
        {
            Name = "Legacy",
            BaseUrl = "https://legacy.example.com",
            Password = "literal-password",
            ApiKey = "literal-key",
            Token = "literal-token"
        }));

        Assert.Equal(3, bag.Items.Count(d => d.Code == DiagnosticCatalog.ConnectorSecretLiteral));
    }

    /// <summary>A username is not a secret, and demanding a reference for one would be noise.</summary>
    [Fact]
    public void AUsernameMayBeALiteral()
    {
        var bag = SchemaValidator.Validate(WithConnector(new ConnectorModel
        {
            Name = "Legacy",
            BaseUrl = "https://legacy.example.com",
            Username = "showcase",
            Password = "${LEGACY_INVENTORY_PASSWORD}"
        }));

        Assert.DoesNotContain(bag.Items, d => d.Code == DiagnosticCatalog.ConnectorSecretLiteral);
    }
}
