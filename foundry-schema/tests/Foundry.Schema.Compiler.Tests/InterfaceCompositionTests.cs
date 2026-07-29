using Foundry.Schema.Compiler;
using Xunit;

namespace Foundry.Schema.Compiler.Tests;

public class InterfaceCompositionTests
{
    [Fact]
    public void DefaultEntity_InheritsBaseEntityObjectId()
    {
        var entity = new Entity
        {
            Name = "TestEntity",
            Properties = new List<Property>
            {
                new Property { Name = "Name", Type = "string" }
            }
        };
        var code = TestHelpers.GenerateForSingleEntity(entity);

        Assert.Contains("BaseEntity<ObjectId>", code);
    }

    [Fact]
    public void EntityWithCustomKeyType_UsesCustomKeyInBaseEntity()
    {
        var entity = new Entity
        {
            Name = "TestEntity",
            Properties = new List<Property>
            {
                new Property { Name = "Id", Type = "Guid", IsKey = true },
                new Property { Name = "Name", Type = "string" }
            }
        };
        var code = TestHelpers.GenerateForSingleEntity(entity);

        Assert.Contains("BaseEntity<Guid>", code);
    }

    [Fact]
    public void AllEntities_IncludeIVersionable()
    {
        var entity = new Entity
        {
            Name = "TestEntity",
            Properties = new List<Property>
            {
                new Property { Name = "Name", Type = "string" }
            }
        };
        var code = TestHelpers.GenerateForSingleEntity(entity);

        Assert.Contains("IVersionable", code);
    }

    [Fact]
    public void SoftDeleteEntity_ImplementsISoftDelete()
    {
        var entity = new Entity
        {
            Name = "TestEntity",
            SoftDelete = true,
            Properties = new List<Property>
            {
                new Property { Name = "Name", Type = "string" }
            }
        };
        var code = TestHelpers.GenerateForSingleEntity(entity);

        Assert.Contains("ISoftDelete", code);
    }

    [Fact]
    public void NonSoftDeleteEntity_DoesNotImplementISoftDelete()
    {
        var entity = new Entity
        {
            Name = "TestEntity",
            SoftDelete = false,
            Properties = new List<Property>
            {
                new Property { Name = "Name", Type = "string" }
            }
        };
        var code = TestHelpers.GenerateForSingleEntity(entity);

        Assert.DoesNotContain("ISoftDelete", code);
    }

    [Fact]
    public void SoftDeleteEntity_InjectsIsDeletedProperty()
    {
        var entity = new Entity
        {
            Name = "TestEntity",
            SoftDelete = true,
            Properties = new List<Property>
            {
                new Property { Name = "Name", Type = "string" }
            }
        };
        var code = TestHelpers.GenerateForSingleEntity(entity);

        Assert.Contains("public bool IsDeleted { get; init; } = false;", code);
    }

    [Fact]
    public void SoftDeleteEntity_InjectsDeletedAtProperty()
    {
        var entity = new Entity
        {
            Name = "TestEntity",
            SoftDelete = true,
            Properties = new List<Property>
            {
                new Property { Name = "Name", Type = "string" }
            }
        };
        var code = TestHelpers.GenerateForSingleEntity(entity);

        Assert.Contains("public DateTime? DeletedAt { get; init; }", code);
    }

    [Fact]
    public void SoftDeleteEntity_IsDeletedHasIndexedAttribute()
    {
        var entity = new Entity
        {
            Name = "TestEntity",
            SoftDelete = true,
            Properties = new List<Property>
            {
                new Property { Name = "Name", Type = "string" }
            }
        };
        var code = TestHelpers.GenerateForSingleEntity(entity);

        // The injected IsDeleted property should have [Indexed]
        // IsDeleted is indexed for the soft-delete filter and [JsonIgnore]d so storage
        // bookkeeping stays off the wire and cannot be set by a caller.
        Assert.Contains("[Indexed]", code);
        Assert.Contains("[JsonIgnore]\n    public bool IsDeleted", code);
        Assert.Contains("[JsonIgnore]\n    public DateTime? DeletedAt", code);
    }

    [Fact]
    public void EntityWithBaseClass_InheritsFromSpecifiedBaseClass()
    {
        var entity = new Entity
        {
            Name = "TestEntity",
            BaseClass = "CustomBaseEntity",
            Properties = new List<Property>
            {
                new Property { Name = "Name", Type = "string" }
            }
        };
        var code = TestHelpers.GenerateForSingleEntity(entity);

        // Both, and BaseEntity first.
        //
        // baseClass used to *replace* BaseEntity<ObjectId>, which left the entity with no Id and no
        // IEntity<ObjectId> -- and every generic in the framework is constrained on that interface,
        // so naming a baseClass took the repository, the endpoint generator and the workflow engine
        // down with it. This assertion was the old behaviour written down, which is why it did not
        // catch it: nothing ever compiled an entity that used the field.
        Assert.Contains("BaseEntity<ObjectId>, CustomBaseEntity", code);
    }

    [Fact]
    public void EntityDeclaration_UsesRecordKeyword()
    {
        var entity = new Entity
        {
            Name = "TestEntity",
            Properties = new List<Property>
            {
                new Property { Name = "Name", Type = "string" }
            }
        };
        var code = TestHelpers.GenerateForSingleEntity(entity);

        Assert.Contains("public partial record TestEntity", code);
    }

    [Fact]
    public void EntityWithBaseClassAndSoftDelete_IncludesBothInInheritanceList()
    {
        var entity = new Entity
        {
            Name = "TestEntity",
            BaseClass = "CustomBase",
            SoftDelete = true,
            Properties = new List<Property>
            {
                new Property { Name = "Name", Type = "string" }
            }
        };
        var code = TestHelpers.GenerateForSingleEntity(entity);

        Assert.Contains("CustomBase", code);
        Assert.Contains("IVersionable", code);
        Assert.Contains("ISoftDelete", code);
    }

    // ── Multi-tenancy ───────────────────────────────────────────────────────

    private static Entity MultiTenantEntity() => new()
    {
        Name = "Invoice",
        MultiTenant = true,
        Properties = new List<Property>
        {
            new() { Name = "Id", Type = "ObjectId", IsKey = true },
            new() { Name = "TenantId", Type = "string", IsTenantKey = true },
            new() { Name = "Reference", Type = "string" }
        }
    };

    [Fact]
    public void MultiTenantEntity_ImplementsIMultiTenant()
    {
        Assert.Contains("IMultiTenant", TestHelpers.GenerateForSingleEntity(MultiTenantEntity()));
    }

    [Fact]
    public void MultiTenantEntity_EmitsTheTenantKeyWithASetter()
    {
        // Not a style preference. IMultiTenant declares `string TenantId { get; set; }`, and an
        // `init` accessor cannot implement `set` (CS8854) -- so while every other emitted property
        // is correctly init-only, this one made the entity fail to compile. `set` is also what lets
        // the repository stamp the tenant from the ambient context instead of trusting the caller.
        var code = TestHelpers.GenerateForSingleEntity(MultiTenantEntity());

        Assert.Contains("public string TenantId { get; set; }", code);
    }

    [Fact]
    public void MultiTenantEntity_LeavesOrdinaryPropertiesInitOnly()
    {
        // The setter is granted to the tenant key alone; caller-supplied data stays immutable.
        var code = TestHelpers.GenerateForSingleEntity(MultiTenantEntity());

        Assert.Contains("public string Reference { get; init; }", code);
    }

    [Fact]
    public void EntityWithoutTenancy_HasNoTenantSetter()
    {
        var entity = new Entity
        {
            Name = "Note",
            Properties = new List<Property>
            {
                new() { Name = "TenantId", Type = "string" }
            }
        };

        var code = TestHelpers.GenerateForSingleEntity(entity);

        Assert.DoesNotContain("IMultiTenant", code);
        Assert.Contains("public string TenantId { get; init; }", code);
    }
}
