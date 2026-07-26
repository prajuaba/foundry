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

        Assert.Contains("CustomBaseEntity", code);
        Assert.DoesNotContain("BaseEntity<ObjectId>", code);
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
}
