using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Foundry.Schema.Compiler;
using MongoDB.Bson;

namespace Foundry.Testing.Generators;

/// <summary>
/// Generates realistic schema-driven mock data and boundary values for test suites.
/// </summary>
/// <remarks>
/// The values produced here have to satisfy the schema they came from. Mock data that violates its
/// own constraints produces generated test suites that fail against a correctly-working API, and the
/// developer then debugs their application rather than the fixture — the most expensive way for this
/// to be wrong.
/// </remarks>
public static class MockDataGenerator
{
    // Random.Shared rather than a shared instance: Random's instance methods are not thread-safe, and
    // concurrent use can corrupt its internal state so that it returns 0 indefinitely. Suite
    // generation across entities is a natural thing to parallelise.
    private static Random Rand => Random.Shared;

    private static readonly Regex MaxLengthPattern =
        new(@"^(?:MaxLength|StringLength)\(\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex RangePattern =
        new(@"^Range\(\s*(-?\d+)\s*,\s*(-?\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static Dictionary<string, object?> GenerateEntityMockData(Entity entity)
    {
        var data = new Dictionary<string, object?>();

        foreach (var prop in entity.Properties)
        {
            if (prop.IsKey)
            {
                // A valid ObjectId, which is what the data layer requires. This used to be
                // Guid.NewGuid().ToString("N") -- 32 hex characters where an ObjectId is 24 -- so the
                // mock key could never be parsed and every generated test that posted or fetched by
                // id failed before exercising anything.
                data[prop.Name] = ObjectId.GenerateNewId().ToString();
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
            return $"user-{Rand.Next(100, 999)}@foundry-test.com";

        if (prop.Attributes.Contains("PiiCreditCard"))
            return "4532-1234-5678-9012";

        return type switch
        {
            // Reference properties: a non-key ObjectId used to fall through to the catch-all and
            // produce the literal "test_data", which is not a parsable id.
            "objectid" => ObjectId.GenerateNewId().ToString(),
            "guid" => Guid.NewGuid().ToString(),
            "string" => GenerateString(prop),
            "int" or "int32" or "long" => GenerateInteger(prop),
            "decimal" or "double" or "float" => GenerateDecimal(prop),
            "bool" or "boolean" => true,
            "datetime" => DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            _ => "test_data"
        };
    }

    /// <summary>
    /// Produces a string that satisfies any length constraint declared on the property.
    /// </summary>
    /// <remarks>
    /// A property carrying <c>[MaxLength(5)]</c> was given the 12-character "sample_value", so the
    /// generated request failed the API's own validation with a 400 and the generated test reported it
    /// as an application defect.
    /// </remarks>
    private static string GenerateString(Property prop)
    {
        var value = prop.Name.Contains("Name", StringComparison.OrdinalIgnoreCase)
            ? "Test Object " + Rand.Next(1, 100)
            : "sample_value";

        var maxLength = FindMaxLength(prop);
        if (maxLength.HasValue && value.Length > maxLength.Value)
        {
            // Still non-empty, so a [Required] constraint continues to hold.
            value = maxLength.Value > 0 ? value.Substring(0, maxLength.Value) : "x";
        }

        return value;
    }

    private static int GenerateInteger(Property prop)
    {
        var range = FindRange(prop);
        return range.HasValue
            ? Rand.Next(range.Value.Min, range.Value.Max == int.MaxValue ? int.MaxValue : range.Value.Max + 1)
            : Rand.Next(1, 1000);
    }

    private static decimal GenerateDecimal(Property prop)
    {
        var range = FindRange(prop);
        if (!range.HasValue) return Math.Round((decimal)(Rand.NextDouble() * 100), 2);

        var span = (decimal)range.Value.Max - range.Value.Min;
        return Math.Round(range.Value.Min + (decimal)Rand.NextDouble() * span, 2);
    }

    private static int? FindMaxLength(Property prop)
    {
        foreach (var attribute in prop.Attributes)
        {
            var match = MaxLengthPattern.Match(attribute ?? string.Empty);
            if (match.Success && int.TryParse(match.Groups[1].Value, out var length)) return length;
        }
        return null;
    }

    private static (int Min, int Max)? FindRange(Property prop)
    {
        foreach (var attribute in prop.Attributes)
        {
            var match = RangePattern.Match(attribute ?? string.Empty);
            if (match.Success
                && int.TryParse(match.Groups[1].Value, out var min)
                && int.TryParse(match.Groups[2].Value, out var max)
                && min <= max)
            {
                return (min, max);
            }
        }
        return null;
    }
}
