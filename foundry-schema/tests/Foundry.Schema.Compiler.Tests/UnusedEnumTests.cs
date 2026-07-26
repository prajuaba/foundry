using Foundry.Schema.Compiler;
using Xunit;

namespace Foundry.Schema.Compiler.Tests;

/// <summary>
/// Covers FDY3010, which flags an enum nothing is typed with.
/// </summary>
/// <remarks>
/// The motivating case came from the eval suite: a model declared TicketPriority and TicketStatus
/// with correct values, then typed both properties as plain strings. Each half is individually
/// valid, so nothing else caught it, and the result was an entity with no type safety and two
/// enums no generated code ever mentioned.
/// </remarks>
public class UnusedEnumTests
{
    private static SchemaModel Schema(List<Foundry.Schema.Compiler.Enum> enums, List<Property> properties) => new()
    {
        Namespace = "Test.Domain",
        Enums = enums,
        Entities = new List<Entity>
        {
            new()
            {
                Name = "Ticket",
                Properties = new List<Property>
                {
                    new() { Name = "Id", Type = "ObjectId", IsKey = true }
                }.Concat(properties).ToList()
            }
        }
    };

    private static Foundry.Schema.Compiler.Enum EnumOf(string name, params string[] values)
        => new() { Name = name, Values = values.ToList() };

    [Fact]
    public void DeclaredButUnusedEnum_IsReported()
    {
        var bag = SchemaValidator.Validate(Schema(
            new List<Foundry.Schema.Compiler.Enum> { EnumOf("TicketPriority", "Low", "High") },
            new List<Property> { new() { Name = "Priority", Type = "string" } }));

        Assert.Contains(bag.Items, d => d.Code == DiagnosticCatalog.UnusedEnum);
    }

    /// <summary>
    /// A warning, not an error: an unused enum still emits a valid C# type, and one may be
    /// referenced only from hand-written *.Custom.cs code.
    /// </summary>
    [Fact]
    public void UnusedEnum_DoesNotFailTheBuild()
    {
        var bag = SchemaValidator.Validate(Schema(
            new List<Foundry.Schema.Compiler.Enum> { EnumOf("TicketPriority", "Low", "High") },
            new List<Property> { new() { Name = "Priority", Type = "string" } }));

        Assert.False(bag.HasErrors);
    }

    /// <summary>
    /// The hint should name the property that most likely meant to use it, or the diagnostic is
    /// just noise the reader scrolls past.
    /// </summary>
    [Fact]
    public void Hint_NamesTheLikelyIntendedProperty()
    {
        var bag = SchemaValidator.Validate(Schema(
            new List<Foundry.Schema.Compiler.Enum> { EnumOf("TicketPriority", "Low", "High") },
            new List<Property> { new() { Name = "Priority", Type = "string" } }));

        var diagnostic = bag.Items.Single(d => d.Code == DiagnosticCatalog.UnusedEnum);
        Assert.Contains("Ticket.Priority", diagnostic.Hint, StringComparison.Ordinal);
        Assert.Contains("TicketPriority", diagnostic.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void EnumUsedByAnEntityProperty_IsNotReported()
    {
        var bag = SchemaValidator.Validate(Schema(
            new List<Foundry.Schema.Compiler.Enum> { EnumOf("TicketPriority", "Low", "High") },
            new List<Property> { new() { Name = "Priority", Type = "TicketPriority", IsEnum = true } }));

        Assert.DoesNotContain(bag.Items, d => d.Code == DiagnosticCatalog.UnusedEnum);
    }

    /// <summary>A DTO reference counts as usage; the enum still reaches generated code.</summary>
    [Fact]
    public void EnumUsedOnlyByADto_IsNotReported()
    {
        var schema = Schema(
            new List<Foundry.Schema.Compiler.Enum> { EnumOf("TicketPriority", "Low", "High") },
            new List<Property>());

        var withDto = schema with
        {
            Dtos = new List<DtoModel>
            {
                new()
                {
                    Name = "TicketSummaryDto",
                    Properties = new List<DtoProperty> { new() { Name = "Priority", Type = "TicketPriority" } }
                }
            }
        };

        Assert.DoesNotContain(SchemaValidator.Validate(withDto).Items, d => d.Code == DiagnosticCatalog.UnusedEnum);
    }

    [Fact]
    public void MatchIsCaseInsensitive()
    {
        var bag = SchemaValidator.Validate(Schema(
            new List<Foundry.Schema.Compiler.Enum> { EnumOf("TicketPriority", "Low", "High") },
            new List<Property> { new() { Name = "Priority", Type = "ticketpriority", IsEnum = true } }));

        Assert.DoesNotContain(bag.Items, d => d.Code == DiagnosticCatalog.UnusedEnum);
    }

    [Fact]
    public void SchemaWithNoEnums_ReportsNothing()
    {
        var bag = SchemaValidator.Validate(Schema(
            new List<Foundry.Schema.Compiler.Enum>(),
            new List<Property> { new() { Name = "Priority", Type = "string" } }));

        Assert.DoesNotContain(bag.Items, d => d.Code == DiagnosticCatalog.UnusedEnum);
    }
}
