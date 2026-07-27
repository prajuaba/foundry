using Foundry.Schema.Compiler;
using Xunit;

namespace Foundry.Schema.Compiler.Tests;

/// <summary>
/// Compilation of row-level ownership.
/// </summary>
public class OwnershipGenerationTests
{
    private static Entity OwnedEntity(params string[] exemptRoles) => new()
    {
        Name = "Note",
        OwnerScoped = true,
        OwnerExemptRoles = new List<string>(exemptRoles),
        Properties = new List<Property>
        {
            new() { Name = "Id", Type = "ObjectId", IsKey = true },
            new() { Name = "OwnerId", Type = "string", IsOwnerKey = true },
            new() { Name = "Body", Type = "string" }
        }
    };

    [Fact]
    public void AnOwnerScopedEntity_ImplementsIOwnedResource()
    {
        Assert.Contains("IOwnedResource", TestHelpers.GenerateForSingleEntity(OwnedEntity()));
    }

    [Fact]
    public void TheOwnerKey_IsEmittedWithASetter()
    {
        // IOwnedResource declares `string OwnerId { get; set; }`, and an `init` accessor cannot
        // implement `set` (CS8854) -- the defect multi-tenancy shipped with for its entire life.
        // `set` is also what lets the repository stamp the owner from the authenticated caller
        // rather than trusting the request body.
        Assert.Contains("public string OwnerId { get; set; }", TestHelpers.GenerateForSingleEntity(OwnedEntity()));
    }

    [Fact]
    public void TheOwnerKey_CarriesTheOwnerKeyAttribute()
    {
        Assert.Contains("[OwnerKey]", TestHelpers.GenerateForSingleEntity(OwnedEntity()));
    }

    [Fact]
    public void OrdinaryProperties_StayInitOnly()
    {
        Assert.Contains("public string Body { get; init; }", TestHelpers.GenerateForSingleEntity(OwnedEntity()));
    }

    [Fact]
    public void ExemptRoles_AreEmittedOnTheEntity()
    {
        var code = TestHelpers.GenerateForSingleEntity(OwnedEntity("Supervisor", "Auditor"));

        Assert.Contains("[OwnerExemptRoles(\"Supervisor\", \"Auditor\")]", code);
    }

    [Fact]
    public void NoExemptRoles_EmitsNoAttribute()
    {
        Assert.DoesNotContain("OwnerExemptRoles", TestHelpers.GenerateForSingleEntity(OwnedEntity()));
    }

    [Fact]
    public void AnExemptRoleContainingAQuote_CannotEscapeTheLiteral()
    {
        // Schema-to-code injection: this compiler has had one already, so every schema-sourced
        // string reaching generated code is escaped rather than interpolated raw.
        //
        // Asserted as the exact emitted attribute. A "does not contain the payload" check would be
        // vacuous here -- the escaped form still contains the payload text, safely inside a literal.
        var code = TestHelpers.GenerateForSingleEntity(OwnedEntity("Sup\"); Environment.Exit(1); //"));

        Assert.Contains("""[OwnerExemptRoles("Sup\"); Environment.Exit(1); //")]""", code);
    }

    [Fact]
    public void AnEntityWithoutOwnership_DoesNotImplementIOwnedResource()
    {
        var entity = new Entity
        {
            Name = "Note",
            Properties = new List<Property>
            {
                new() { Name = "Id", Type = "ObjectId", IsKey = true },
                new() { Name = "Body", Type = "string" }
            }
        };

        Assert.DoesNotContain("IOwnedResource", TestHelpers.GenerateForSingleEntity(entity));
    }
}

/// <summary>
/// IR rules for ownership and for the tenant key's name.
/// </summary>
/// <remarks>
/// The state each rule exists to prevent is the half-configured one, which reads as protected and
/// is not.
/// </remarks>
public class OwnershipValidationTests
{
    private static SchemaModel SchemaWith(Entity entity) => new()
    {
        Namespace = "Test.Domain",
        Entities = new List<Entity> { entity }
    };

    [Fact]
    public void OwnerScopedWithNoOwnerKey_IsAnError()
    {
        var diagnostics = SchemaValidator.Validate(SchemaWith(new Entity
        {
            Name = "Note",
            OwnerScoped = true,
            Properties = new List<Property>
            {
                new() { Name = "Id", Type = "ObjectId", IsKey = true },
                new() { Name = "Body", Type = "string" }
            }
        }));

        Assert.True(diagnostics.HasErrors);
        Assert.Contains(DiagnosticCatalog.OwnerScopedWithoutOwnerKey, diagnostics.Render());
    }

    [Fact]
    public void AnOwnerKeyWithoutOwnerScoped_IsAWarning()
    {
        // Reads as owner-scoped to anyone skimming the document, while every query returns every row.
        var diagnostics = SchemaValidator.Validate(SchemaWith(new Entity
        {
            Name = "Note",
            Properties = new List<Property>
            {
                new() { Name = "Id", Type = "ObjectId", IsKey = true },
                new() { Name = "OwnerId", Type = "string", IsOwnerKey = true }
            }
        }));

        Assert.Contains(DiagnosticCatalog.OwnerKeyWithoutOwnerScoped, diagnostics.Render());
    }

    [Fact]
    public void AnOwnerKeyNamedAnythingElse_IsAnError()
    {
        // The data layer filters on the stored field by name, so another name yields a filter that
        // matches no document -- an empty read, or a write scoped by a predicate that never matches.
        var diagnostics = SchemaValidator.Validate(SchemaWith(new Entity
        {
            Name = "Note",
            OwnerScoped = true,
            Properties = new List<Property>
            {
                new() { Name = "Id", Type = "ObjectId", IsKey = true },
                new() { Name = "CreatedBy", Type = "string", IsOwnerKey = true }
            }
        }));

        Assert.True(diagnostics.HasErrors);
        Assert.Contains(DiagnosticCatalog.OwnerKeyMustBeNamedOwnerId, diagnostics.Render());
    }

    [Fact]
    public void ANonStringOwnerKey_IsAnError()
    {
        var diagnostics = SchemaValidator.Validate(SchemaWith(new Entity
        {
            Name = "Note",
            OwnerScoped = true,
            Properties = new List<Property>
            {
                new() { Name = "Id", Type = "ObjectId", IsKey = true },
                new() { Name = "OwnerId", Type = "int", IsOwnerKey = true }
            }
        }));

        Assert.True(diagnostics.HasErrors);
    }

    [Fact]
    public void ExemptRolesWithoutOwnerScoped_IsAWarning()
    {
        var diagnostics = SchemaValidator.Validate(SchemaWith(new Entity
        {
            Name = "Note",
            OwnerExemptRoles = new List<string> { "Supervisor" },
            Properties = new List<Property>
            {
                new() { Name = "Id", Type = "ObjectId", IsKey = true },
                new() { Name = "Body", Type = "string" }
            }
        }));

        Assert.Contains(DiagnosticCatalog.OwnerExemptRolesWithoutOwnerScoped, diagnostics.Render());
    }

    [Fact]
    public void AFullyConfiguredOwnedEntity_Validates()
    {
        var diagnostics = SchemaValidator.Validate(SchemaWith(new Entity
        {
            Name = "Note",
            OwnerScoped = true,
            OwnerExemptRoles = new List<string> { "Supervisor" },
            Properties = new List<Property>
            {
                new() { Name = "Id", Type = "ObjectId", IsKey = true },
                new() { Name = "OwnerId", Type = "string", IsOwnerKey = true },
                new() { Name = "Body", Type = "string" }
            }
        }));

        Assert.False(diagnostics.HasErrors, diagnostics.Render());
    }

    [Fact]
    public void ATenantKeyNamedAnythingElse_IsAnError()
    {
        // Pre-existing, and found while building ownership. The emitted entity does not satisfy
        // IMultiTenant (CS0535), so a schema naming its tenant key anything but 'TenantId' produced
        // a project that could not build -- and had it built, the repository's filter on "TenantId"
        // would have matched no document. Caught here, where the message can name the property.
        var diagnostics = SchemaValidator.Validate(SchemaWith(new Entity
        {
            Name = "Invoice",
            MultiTenant = true,
            Properties = new List<Property>
            {
                new() { Name = "Id", Type = "ObjectId", IsKey = true },
                new() { Name = "CompanyId", Type = "string", IsTenantKey = true }
            }
        }));

        Assert.True(diagnostics.HasErrors);
        Assert.Contains(DiagnosticCatalog.TenantKeyMustBeNamedTenantId, diagnostics.Render());
    }

    [Fact]
    public void ATenantKeyNamedTenantId_Validates()
    {
        var diagnostics = SchemaValidator.Validate(SchemaWith(new Entity
        {
            Name = "Invoice",
            MultiTenant = true,
            Properties = new List<Property>
            {
                new() { Name = "Id", Type = "ObjectId", IsKey = true },
                new() { Name = "TenantId", Type = "string", IsTenantKey = true }
            }
        }));

        Assert.False(diagnostics.HasErrors, diagnostics.Render());
    }
}
