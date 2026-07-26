using Foundry.Schema.Compiler;

namespace Foundry.Schema.Compiler.Tests;

/// <summary>
/// Shared helper methods for building minimal schemas and invoking the generator.
/// </summary>
public static class TestHelpers
{
    /// <summary>
    /// Builds a SchemaModel with a single entity, runs Generate(), and returns the
    /// generated code string for that entity.
    /// </summary>
    public static string GenerateForSingleEntity(Entity entity, string ns = "TestNamespace")
    {
        var schema = new SchemaModel
        {
            Namespace = ns,
            Entities = new List<Entity> { entity }
        };
        var result = PocoGenerator.Generate(schema);
        return result[entity.Name];
    }

    /// <summary>
    /// Builds a SchemaModel with a single enum, runs Generate(), and returns the
    /// generated code string for that enum.
    /// </summary>
    public static string GenerateForSingleEnum(Foundry.Schema.Compiler.Enum enumDef, string ns = "TestNamespace")
    {
        var schema = new SchemaModel
        {
            Namespace = ns,
            Enums = new List<Foundry.Schema.Compiler.Enum> { enumDef }
        };
        var result = PocoGenerator.Generate(schema);
        return result[enumDef.Name];
    }

    /// <summary>
    /// Creates a minimal entity with a single property of the given type.
    /// </summary>
    public static Entity MakeEntityWithProperty(string propName, string propType,
        bool isKey = false, bool isEnum = false, List<string>? attributes = null)
    {
        return new Entity
        {
            Name = "TestEntity",
            Properties = new List<Property>
            {
                new Property
                {
                    Name = propName,
                    Type = propType,
                    IsKey = isKey,
                    IsEnum = isEnum,
                    Attributes = attributes ?? new List<string>()
                }
            }
        };
    }
    /// <summary>
    /// Builds a SchemaModel with a single DTO, runs Generate(), and returns the
    /// generated code string for that DTO.
    /// </summary>
    public static string GenerateForSingleDto(DtoModel dto, string ns = "TestNamespace")
    {
        var schema = new SchemaModel
        {
            Namespace = ns,
            Dtos = new List<DtoModel> { dto }
        };
        var result = PocoGenerator.Generate(schema);
        return result[dto.Name];
    }

    /// <summary>
    /// Builds a SchemaModel with a single custom endpoint, runs Generate(), and returns the
    /// generated code string for its handler.
    /// </summary>
    public static string GenerateForSingleCustomEndpoint(CustomEndpoint ep, string ns = "TestNamespace")
    {
        var schema = new SchemaModel
        {
            Namespace = ns,
            CustomEndpoints = new List<CustomEndpoint> { ep }
        };
        var result = PocoGenerator.Generate(schema);
        return result[$"Handlers/{ep.RequestType}Handler"];
    }
}
