using Foundry.Schema.Compiler;
using Xunit;

namespace Foundry.Schema.Compiler.Tests;

/// <summary>
/// Resource-level authorization past a single owner: grants, and read-only exemption.
/// </summary>
/// <remarks>
/// <para>
/// <c>ownerScoped</c> answered "this row belongs to one caller" and nothing beyond it. Sharing,
/// delegation and team scoping had nowhere to be expressed, so a schema needing any of them wrote the
/// rule by hand in a business rule — where nothing checked it had been applied to every read path.
/// </para>
/// <para>
/// They are one mechanism here, because they differ only in what a grant names: a subject id shares
/// with a person, a group id shares with a team. The caller's identities are their subject plus the
/// groups their token carries.
/// </para>
/// </remarks>
public class GrantTests
{
    private static SchemaModel Schema(bool ownerScoped = true, string grantType = "List<string>",
        string grantName = "SharedWith", string[]? exempt = null, string[]? readExempt = null) => new()
    {
        Namespace = "Test.Domain",
        Entities =
        [
            new Entity
            {
                Name = "Note",
                OwnerScoped = ownerScoped,
                OwnerExemptRoles = [.. exempt ?? []],
                OwnerReadExemptRoles = [.. readExempt ?? []],
                ApiEnabledMethods = ["GET"],
                Properties =
                [
                    new Property { Name = "Id", Type = "ObjectId", IsKey = true },
                    new Property { Name = "OwnerId", Type = "string", IsOwnerKey = true },
                    new Property { Name = grantName, Type = grantType, IsSharedWithKey = true },
                    new Property { Name = "Body", Type = "string" }
                ]
            }
        ]
    };

    private static string EntityCode(SchemaModel schema) => PocoGenerator.Generate(schema)["Note"];

    // ── Emission ────────────────────────────────────────────────────────────

    [Fact]
    public void AShareableEntityImplementsISharedResource()
    {
        var code = EntityCode(Schema());

        Assert.Contains("ISharedResource", code);

        // Not both: ISharedResource extends IOwnedResource, and listing each would be two names for
        // one contract.
        Assert.DoesNotContain("IOwnedResource", code);
    }

    [Fact]
    public void TheGrantSetIsSettable()
    {
        // ISharedResource declares `List<string> SharedWith { get; set; }`, and C# will not accept an
        // init accessor as an implementation of set. That is CS8854 — exactly what made every
        // multi-tenant entity the compiler emitted fail to build.
        var code = EntityCode(Schema());

        Assert.Contains("[SharedWithKey]", code);
        Assert.Matches(@"List<string> SharedWith \{ get; set;", code);
    }

    [Fact]
    public void AnEntityWithoutAGrantSetIsUnchanged()
    {
        var schema = Schema();
        schema.Entities[0].Properties.RemoveAt(2);

        var code = EntityCode(schema);

        Assert.Contains("IOwnedResource", code);
        Assert.DoesNotContain("ISharedResource", code);
        Assert.DoesNotContain("SharedWithKey", code);
    }

    [Fact]
    public void ReadOnlyExemptRolesAreCarriedOntoTheEntity()
    {
        // The auditor case, which could not be stated at all: ownerExemptRoles is per entity, so a
        // role exempted for reads was exempted for updates and deletes too.
        var code = EntityCode(Schema(readExempt: ["Auditor"]));

        Assert.Contains("[OwnerReadExemptRoles(\"Auditor\")]", code);
    }

    [Fact]
    public void BothExemptionKindsCanBeCarriedAtOnce()
    {
        var code = EntityCode(Schema(exempt: ["Supervisor"], readExempt: ["Auditor"]));

        Assert.Contains("[OwnerExemptRoles(\"Supervisor\")]", code);
        Assert.Contains("[OwnerReadExemptRoles(\"Auditor\")]", code);
    }

    [Fact]
    public void ARoleNameContainingAQuoteCannotEscapeTheAttribute()
    {
        // Same class as the schema-to-code injection this compiler has had once.
        var code = EntityCode(Schema(readExempt: ["Auditor\", Evil = \"x"]));

        Assert.DoesNotContain("Evil = \"x\")]", code);
    }

    // ── Validation ──────────────────────────────────────────────────────────

    [Fact]
    public void AGrantSetOnAnEntityThatIsNotOwnerScopedWarns()
    {
        // Without ownership every caller already sees every row, so the document reads as if access
        // were restricted while nothing restricts it — the same shape as an owner key on an entity
        // that is not owner-scoped.
        var diagnostics = SchemaValidator.Validate(Schema(ownerScoped: false));

        Assert.Contains(diagnostics.Items, d => d.Code == DiagnosticCatalog.SharedWithKeyWithoutOwnerScoped);
    }

    [Fact]
    public void AGrantSetMustBeNamedSharedWith()
    {
        var diagnostics = SchemaValidator.Validate(Schema(grantName: "Viewers"));

        Assert.Contains(diagnostics.Items, d =>
            d.Code == DiagnosticCatalog.SharedWithKeyShape && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void AGrantSetMustBeAListOfStrings()
    {
        // Any other element type produces a filter matching no documents: a grant declared, stored,
        // and silently never consulted.
        var diagnostics = SchemaValidator.Validate(Schema(grantType: "string"));

        Assert.Contains(diagnostics.Items, d =>
            d.Code == DiagnosticCatalog.SharedWithKeyShape && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void AWellFormedGrantSetValidates()
    {
        var diagnostics = SchemaValidator.Validate(Schema());

        Assert.DoesNotContain(diagnostics.Items, d => d.Severity == DiagnosticSeverity.Error);
    }

    // ── Masked property categories ──────────────────────────────────────────

    private static SchemaModel MaskedSchema(string? category) => new()
    {
        Namespace = "Test.Domain",
        Entities =
        [
            new Entity
            {
                Name = "Claim",
                ApiEnabledMethods = ["GET"],
                Properties =
                [
                    new Property { Name = "Id", Type = "ObjectId", IsKey = true },
                    new Property
                    {
                        Name = "CardNumber", Type = "string",
                        Attributes = ["Mask"], SensitiveCategory = category
                    }
                ]
            }
        ]
    };

    [Fact]
    public void ADeclaredCategoryIsCarriedOntoTheAttribute()
    {
        // Masking was one switch: view:pii unmasked every masked property on every entity, so
        // letting someone read one field meant letting them read all of them.
        var code = PocoGenerator.Generate(MaskedSchema("financial"))["Claim"];

        Assert.Contains("Category = \"financial\"", code);
    }

    [Fact]
    public void NoCategoryLeavesTheAttributeDefault()
    {
        // Back-compatible on purpose: the attribute defaults to "pii", so every existing declaration
        // keeps answering to view:pii exactly as it did.
        var code = PocoGenerator.Generate(MaskedSchema(null))["Claim"];

        Assert.Contains("[SensitiveData(Protection = ProtectionType.Mask)]", code);
        Assert.DoesNotContain("Category", code);
    }

    [Fact]
    public void ACategoryContainingAQuoteCannotEscapeTheAttribute()
    {
        var code = PocoGenerator.Generate(MaskedSchema("financial\", Evil = \"x"))["Claim"];

        Assert.DoesNotContain("Evil = \"x\")]", code);
    }

    [Fact]
    public void ARoleThatIsBothFullyAndReadOnlyExemptWarns()
    {
        // It is fully exempt, so the read-only listing describes a restriction that is not applied.
        // Silence would leave a document reading as though an auditor cannot write when it can.
        var diagnostics = SchemaValidator.Validate(
            Schema(exempt: ["Supervisor"], readExempt: ["Supervisor"]));

        Assert.Contains(diagnostics.Items, d => d.Code == DiagnosticCatalog.OwnerExemptRoleAlsoReadExempt);
    }
}
