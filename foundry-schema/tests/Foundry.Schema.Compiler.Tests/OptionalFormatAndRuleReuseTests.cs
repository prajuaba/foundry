using Foundry.Schema.Compiler;
using Xunit;

namespace Foundry.Schema.Compiler.Tests;

/// <summary>
/// Three defects a schema could not express its way around, all found building one application.
/// </summary>
public class OptionalFormatAndRuleReuseTests
{
    // ---- Format validators on optional properties ----

    [Theory]
    [InlineData("Url", "UrlWhenPresent")]
    [InlineData("Phone", "PhoneWhenPresent")]
    [InlineData("Email", "EmailAddressWhenPresent")]
    public void AFormatValidatorChecksShapeOnlyWhenAValueIsPresent(string attribute, string emitted)
    {
        // A generated property is a non-nullable string initialised to string.Empty, and the stock
        // [Url]/[Phone]/[EmailAddress] reject "". Declaring one on an optional property therefore
        // made it mandatory -- a POST omitting the field answered 400 naming something the schema
        // never said was required -- and the only way out was to drop the validation entirely.
        var entity = TestHelpers.MakeEntityWithProperty(
            "Contact", "string", attributes: new List<string> { attribute });
        entity.Properties.Add(new Property { Name = "Id", Type = "ObjectId", IsKey = true });

        var code = TestHelpers.GenerateForSingleEntity(entity);

        Assert.Contains($"[{emitted}]", code);
    }

    [Fact]
    public void RequiredStillRejectsAnEmptyValue()
    {
        // Presence and shape are separate questions. Making the format validator permissive is only
        // correct because [Required] is what says a value must be there at all.
        var entity = TestHelpers.MakeEntityWithProperty(
            "Contact", "string", attributes: new List<string> { "Required", "Url" });
        entity.Properties.Add(new Property { Name = "Id", Type = "ObjectId", IsKey = true });

        var code = TestHelpers.GenerateForSingleEntity(entity);

        Assert.Contains("[Required]", code);
        Assert.Contains("[UrlWhenPresent]", code);
    }

    // ---- One rule name used for more than one request ----

    private static SchemaModel SchemaWithRuleOn(params string[] methods)
    {
        var rules = new Dictionary<string, List<string>>();
        foreach (var m in methods) rules[m] = new List<string> { "SharedRule" };

        return new SchemaModel
        {
            Namespace = "Reuse.Test",
            Entities = new List<Entity>
            {
                new()
                {
                    Name = "Invoice",
                    ApiEnabledMethods = new List<string>(methods),
                    ApiBusinessRules = rules,
                    Properties = new List<Property>
                    {
                        new() { Name = "Id", Type = "ObjectId", IsKey = true },
                        new() { Name = "Total", Type = "decimal" },
                    },
                },
            },
        };
    }

    [Fact]
    public void OneRuleNamedForTwoMethodsImplementsBothInterfaces()
    {
        // "POST": ["XRule"], "PUT": ["XRule"] is the obvious way to say one policy guards both.
        // The stub was written per usage into the same path, so the second overwrote the first and
        // the surviving class implemented one interface while both registrations were emitted --
        // CS0311, on generated code naming types the author never wrote.
        var files = PocoGenerator.Generate(SchemaWithRuleOn("POST", "PUT"));

        var rule = files["Rules/SharedRule"];

        Assert.Contains("IBusinessRule<InsertCommand<Reuse.Test.Invoice>>", rule);
        Assert.Contains("IBusinessRule<UpdateCommand<Reuse.Test.Invoice>>", rule);
    }

    [Fact]
    public void ItGetsOneValidateAsyncPerRequestShape()
    {
        var rule = PocoGenerator.Generate(SchemaWithRuleOn("POST", "PUT"))["Rules/SharedRule"];

        // InsertCommand<T> and UpdateCommand<T> are different types, so the policy may legitimately
        // differ between them and each needs its own body.
        Assert.Contains("ValidateAsync(InsertCommand<Reuse.Test.Invoice> request", rule);
        Assert.Contains("ValidateAsync(UpdateCommand<Reuse.Test.Invoice> request", rule);
    }

    [Fact]
    public void EveryRegistrationHasAMatchingInterface()
    {
        // The precise shape of the CS0311: registrations were emitted per usage and the class
        // implemented only one of them.
        var files = PocoGenerator.Generate(SchemaWithRuleOn("POST", "PUT", "DELETE"));
        var rule = files["Rules/SharedRule"];
        var registrations = files["Rules/RuleRegistrations"];

        foreach (var command in new[] { "InsertCommand", "UpdateCommand", "DeleteCommand" })
        {
            Assert.Contains($"IBusinessRule<{command}<Reuse.Test.Invoice>>, Reuse.Test.Rules.SharedRule", registrations);
            Assert.Contains($"IBusinessRule<{command}<Reuse.Test.Invoice>>", rule);
        }
    }

    [Fact]
    public void ARuleUsedOnceIsUnchanged()
    {
        var rule = PocoGenerator.Generate(SchemaWithRuleOn("POST"))["Rules/SharedRule"];

        Assert.Contains("IBusinessRule<InsertCommand<Reuse.Test.Invoice>>", rule);
        Assert.DoesNotContain("UpdateCommand", rule);
    }
}
