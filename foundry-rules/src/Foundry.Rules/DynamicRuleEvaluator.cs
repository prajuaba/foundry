using System;
using System.Globalization;
using System.Linq;
using System.Reflection;

namespace Foundry.Rules;

/// <summary>
/// Utility engine for dynamically evaluating string, numeric, and enum comparisons on target objects using reflection.
/// </summary>
public static class DynamicRuleEvaluator
{
    /// <summary>
    /// Evaluates a specific property comparison against the target object.
    /// </summary>
    /// <param name="target">The target object containing the property to validate.</param>
    /// <param name="propertyName">The name of the property (case-insensitive).</param>
    /// <param name="op">The comparison operator (e.g., "==", "lessthan", "contains").</param>
    /// <param name="expectedValue">The expected value representation as a string.</param>
    /// <returns>True if the comparison passes; otherwise, false.</returns>
    public static bool Evaluate(object target, string propertyName, string op, string expectedValue)
    {
        expectedValue ??= string.Empty;
        if (target == null || string.IsNullOrEmpty(propertyName))
            return false;

        var propertyInfo = target.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(p => string.Equals(p.Name, propertyName, StringComparison.OrdinalIgnoreCase));

        if (propertyInfo == null)
            return false;

        var propertyValue = propertyInfo.GetValue(target);

        // Handle null values
        if (propertyValue == null)
        {
            return string.IsNullOrEmpty(expectedValue) || 
                   string.Equals(expectedValue, "null", StringComparison.OrdinalIgnoreCase);
        }

        var propertyType = propertyValue.GetType();

        // Handle Enum properties
        if (propertyType.IsEnum)
        {
            if (!Enum.TryParse(propertyType, expectedValue, true, out var enumValue))
                return false;

            return Equals(propertyValue, enumValue);
        }

        // Handle numeric properties
        if (IsNumericType(propertyType))
        {
            if (!decimal.TryParse(propertyValue.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var currentValue))
                return false;

            if (!decimal.TryParse(expectedValue, NumberStyles.Any, CultureInfo.InvariantCulture, out var expectedDecimal))
                return false;

            switch (op.ToLowerInvariant())
            {
                case "equal":
                case "==":
                case "equals":
                    return currentValue == expectedDecimal;
                case "notequal":
                case "!=":
                case "notequals":
                    return currentValue != expectedDecimal;
                case "lessthan":
                case "<":
                    return currentValue < expectedDecimal;
                case "lessthanorequal":
                case "<=":
                    return currentValue <= expectedDecimal;
                case "greaterthan":
                case ">":
                    return currentValue > expectedDecimal;
                case "greaterthanorequal":
                case ">=":
                    return currentValue >= expectedDecimal;
                default:
                    return false;
            }
        }

        // Handle string/other properties
        var stringValue = propertyValue.ToString() ?? string.Empty;
        
        switch (op.ToLowerInvariant())
        {
            case "equal":
            case "==":
            case "equals":
                return string.Equals(stringValue, expectedValue, StringComparison.OrdinalIgnoreCase);
            case "notequal":
            case "!=":
            case "notequals":
                return !string.Equals(stringValue, expectedValue, StringComparison.OrdinalIgnoreCase);
            case "contains":
                return stringValue.IndexOf(expectedValue, StringComparison.OrdinalIgnoreCase) >= 0;
            case "startswith":
                return stringValue.StartsWith(expectedValue, StringComparison.OrdinalIgnoreCase);
            case "endswith":
                return stringValue.EndsWith(expectedValue, StringComparison.OrdinalIgnoreCase);
            default:
                return false;
        }
    }

    private static bool IsNumericType(Type type)
    {
        if (type == null) return false;

        var underlyingType = Nullable.GetUnderlyingType(type) ?? type;

        return underlyingType == typeof(byte) ||
               underlyingType == typeof(sbyte) ||
               underlyingType == typeof(short) ||
               underlyingType == typeof(int) ||
               underlyingType == typeof(long) ||
               underlyingType == typeof(ushort) ||
               underlyingType == typeof(uint) ||
               underlyingType == typeof(ulong) ||
               underlyingType == typeof(float) ||
               underlyingType == typeof(double) ||
               underlyingType == typeof(decimal);
    }
}
