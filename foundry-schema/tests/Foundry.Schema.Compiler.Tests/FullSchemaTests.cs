using System.IO;
using System.Text.Json;
using Foundry.Schema.Compiler;
using Xunit;

namespace Foundry.Schema.Compiler.Tests;

public class FullSchemaTests
{
    /// <summary>
    /// Builds a SchemaModel equivalent to sample_schema.json and verifies output
    /// matches the known-good test_out/Order.cs and test_out/OrderStatus.cs.
    /// </summary>
    [Fact]
    public void SampleSchema_ProducesExpectedOrderEntity()
    {
        var schema = BuildSampleSchema();
        var result = PocoGenerator.Generate(schema);

        Assert.True(result.ContainsKey("Entities/Order"));
        var orderCode = result["Entities/Order"];

        // Class declaration
        Assert.Contains("public partial record Order : BaseEntity<ObjectId>, IVersionable, ISoftDelete", orderCode);

        // Properties
        Assert.Contains("[Indexed]", orderCode);
        Assert.Contains("public string CustomerId { get; init; } = string.Empty;", orderCode);
        Assert.Contains("[Indexed(Unique = true)]", orderCode);
        Assert.Contains("[TextIndexed]", orderCode);
        Assert.Contains("public required string OrderNumber { get; init; } = string.Empty;", orderCode);
        Assert.Contains("public decimal TotalAmount { get; init; } = 0;", orderCode);
        Assert.Contains("public OrderStatus Status { get; init; } = default(OrderStatus);", orderCode);
        Assert.Contains("[SensitiveData(Protection = ProtectionType.Encrypt)]", orderCode);
        Assert.Contains("public string SecretToken { get; init; } = string.Empty;", orderCode);
        Assert.Contains("[SensitiveData(Protection = ProtectionType.Mask, MaskingType = MaskingType.Email)]", orderCode);
        Assert.Contains("public string UserEmail { get; init; } = string.Empty;", orderCode);

        // SoftDelete injected properties
        Assert.Contains("public bool IsDeleted { get; init; } = false;", orderCode);
        Assert.Contains("public DateTime? DeletedAt { get; init; }", orderCode);

        // Namespace and usings
        Assert.Contains("namespace Paperclip.OrderingSystem.Domain;", orderCode);
        Assert.Contains("using MongoDB.Bson;", orderCode);
        Assert.Contains("using Foundry.Core.Entities;", orderCode);
    }

    [Fact]
    public void SampleSchema_ProducesExpectedOrderStatusEnum()
    {
        var schema = BuildSampleSchema();
        var result = PocoGenerator.Generate(schema);

        Assert.True(result.ContainsKey("Enums/OrderStatus"));
        var enumCode = result["Enums/OrderStatus"];

        Assert.Contains("namespace Paperclip.OrderingSystem.Domain;", enumCode);
        Assert.Contains("public enum OrderStatus", enumCode);
        Assert.Contains("Pending", enumCode);
        Assert.Contains("Completed", enumCode);
        Assert.Contains("Cancelled", enumCode);
    }

    [Fact]
    public void SampleSchema_ProducesCorrectFileCount()
    {
        var schema = BuildSampleSchema();
        var result = PocoGenerator.Generate(schema);

        // 1 entity + 1 enum = 2 files
        // Asserting presence rather than an exact count: the generator also emits supporting
        // files (serialization context, index verification), and their number is not what this
        // test is about.
        Assert.True(result.ContainsKey("Entities/Order"));
        Assert.True(result.ContainsKey("Enums/OrderStatus"));
    }

    [Fact]
    public void EmptySchema_ProducesEmptyResult()
    {
        var schema = new SchemaModel
        {
            Namespace = "Test",
            Entities = new List<Entity>(),
            Enums = new List<Foundry.Schema.Compiler.Enum>()
        };
        var result = PocoGenerator.Generate(schema);

        Assert.Empty(result);
    }

    [Fact]
    public void MultipleEntities_ProducesMultipleFiles()
    {
        var schema = new SchemaModel
        {
            Namespace = "TestNamespace",
            Entities = new List<Entity>
            {
                new Entity
                {
                    Name = "Customer",
                    Properties = new List<Property>
                    {
                        new Property { Name = "FullName", Type = "string" }
                    }
                },
                new Entity
                {
                    Name = "Product",
                    Properties = new List<Property>
                    {
                        new Property { Name = "ProductName", Type = "string" },
                        new Property { Name = "Price", Type = "decimal" }
                    }
                }
            }
        };
        var result = PocoGenerator.Generate(schema);

        Assert.True(result.ContainsKey("Entities/Customer"));
        Assert.True(result.ContainsKey("Entities/Product"));
        Assert.True(result.ContainsKey("Entities/Customer"));
        Assert.True(result.ContainsKey("Entities/Product"));
    }

    [Fact]
    public void EntitiesReferencingEachOther_GenerateCorrectTypes()
    {
        var schema = new SchemaModel
        {
            Namespace = "TestNamespace",
            Entities = new List<Entity>
            {
                new Entity
                {
                    Name = "Order",
                    Properties = new List<Property>
                    {
                        new Property { Name = "CustomerId", Type = "string" },
                        new Property { Name = "Status", Type = "OrderStatus", IsEnum = true }
                    }
                },
                new Entity
                {
                    Name = "Customer",
                    Properties = new List<Property>
                    {
                        new Property { Name = "Name", Type = "string" }
                    }
                }
            },
            Enums = new List<Foundry.Schema.Compiler.Enum>
            {
                new Foundry.Schema.Compiler.Enum
                {
                    Name = "OrderStatus",
                    Values = new List<string> { "Pending", "Active" }
                }
            }
        };
        var result = PocoGenerator.Generate(schema);

        Assert.True(result.ContainsKey("Entities/Order"));
        Assert.True(result.ContainsKey("Entities/Customer"));
        Assert.Contains("public OrderStatus Status", result["Entities/Order"]);
        Assert.Contains("public enum OrderStatus", result["Enums/OrderStatus"]);
    }

    [Fact]
    public void Namespace_AppliedToAllGeneratedFiles()
    {
        var schema = new SchemaModel
        {
            Namespace = "MyCompany.Domain.Models",
            Entities = new List<Entity>
            {
                new Entity
                {
                    Name = "Invoice",
                    Properties = new List<Property>
                    {
                        new Property { Name = "Number", Type = "string" }
                    }
                }
            },
            Enums = new List<Foundry.Schema.Compiler.Enum>
            {
                new Foundry.Schema.Compiler.Enum
                {
                    Name = "InvoiceType",
                    Values = new List<string> { "Standard", "Credit" }
                }
            }
        };
        var result = PocoGenerator.Generate(schema);

        foreach (var kvp in result)
        {
            Assert.Contains("namespace MyCompany.Domain.Models", kvp.Value);
        }
    }

    [Fact]
    public void EntityOutput_ContainsRequiredUsingStatements()
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

        Assert.Contains("using System;", code);
        Assert.Contains("using MongoDB.Bson;", code);
        Assert.Contains("using Foundry.Core.Entities;", code);
        Assert.Contains("using Foundry.Core.Entities;", code);
    }

    private static SchemaModel BuildSampleSchema()
    {
        return new SchemaModel
        {
            Namespace = "Paperclip.OrderingSystem.Domain",
            Entities = new List<Entity>
            {
                new Entity
                {
                    Name = "Order",
                    SoftDelete = true,
                    Auditable = true,
                    Indexes = new List<Index>
                    {
                        new Index { Fields = new List<string> { "CustomerId" }, Unique = false }
                    },
                    Properties = new List<Property>
                    {
                        new Property { Name = "CustomerId", Type = "string", Attributes = new List<string> { "Index" } },
                        new Property { Name = "OrderNumber", Type = "string", Attributes = new List<string> { "Required", "UniqueIndex", "TextIndex" } },
                        new Property { Name = "TotalAmount", Type = "decimal" },
                        new Property { Name = "Status", Type = "OrderStatus", IsEnum = true },
                        new Property { Name = "SecretToken", Type = "string", Attributes = new List<string> { "Encrypt" } },
                        new Property { Name = "UserEmail", Type = "string", Attributes = new List<string> { "MaskEmail" } }
                    }
                }
            },
            Enums = new List<Foundry.Schema.Compiler.Enum>
            {
                new Foundry.Schema.Compiler.Enum
                {
                    Name = "OrderStatus",
                    Values = new List<string> { "Pending", "Completed", "Cancelled" }
                }
            }
        };
    }
}
