using System;
using System.Collections.Generic;
using System.Linq;
using Foundry.Schema.Compiler;

namespace Foundry.Testing.Generators;

/// <summary>
/// Generates realistic schema-driven mock data and boundary values for test suites.
/// </summary>
public static class MockDataGenerator
{
    private static readonly Random _rand = new();

    public static Dictionary<string, object?> GenerateEntityMockData(Entity entity)
    {
        var data = new Dictionary<string, object?>();

        foreach (var prop in entity.Properties)
        {
            if (prop.IsKey)
            {
                data[prop.Name] = Guid.NewGuid().ToString("N");
                continue;
            }

            if (prop.IsTenantKey || prop.Attributes.Contains("TenantKey") || prop.Name.Equals(entity.TenantProperty, StringComparison.OrdinalIgnoreCase))
            {
                data[prop.Name] = "tenant-test-1";
                continue;
            }

            data[prop.Name] = GeneratePropertyValue(prop);
        }

        return data;
    }

    private static object GeneratePropertyValue(Property prop)
    {
        var type = prop.Type.ToLowerInvariant().Replace("?", "");

        if (prop.Attributes.Contains("PiiEmail") || prop.Attributes.Contains("MaskEmail"))
            return $"user-{_rand.Next(100, 999)}@foundry-test.com";

        if (prop.Attributes.Contains("PiiCreditCard"))
            return "4532-1234-5678-9012";

        return type switch
        {
            "string" => prop.Name.Contains("Name", StringComparison.OrdinalIgnoreCase) ? "Test Object " + _rand.Next(1, 100) : "sample_value",
            "int" or "int32" or "long" => _rand.Next(1, 1000),
            "decimal" or "double" or "float" => Math.Round((decimal)(_rand.NextDouble() * 100), 2),
            "bool" or "boolean" => true,
            "datetime" => DateTime.UtcNow.ToString("o"),
            _ => "test_data"
        };
    }
}
