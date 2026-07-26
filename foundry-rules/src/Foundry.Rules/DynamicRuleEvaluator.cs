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

        // Handle null values.
        //
        // This previously answered "does the expected value look like null?" and ignored the
        // operator entirely, so `Notes notequal null` reported true for a null Notes -- the exact
        // opposite of what it asserts. Ordering and substring operators have no meaning against
        // null and are false rather than throwing.
        if (propertyValue == null)
        {
            var expectedIsNull = string.IsNullOrEmpty(expectedValue)
                || string.Equals(expectedValue, "null", StringComparison.OrdinalIgnoreCase);

            return op.ToLowerInvariant() switch
            {
                "equal" or "==" or "equals" => expectedIsNull,
                "notequal" or "!=" or "notequals" => !expectedIsNull,
                _ => false
            };
        }

        var propertyType = propertyValue.GetType();

        // Handle Enum properties.
        //
        // Must precede the numeric branch: an enum's TypeCode is that of its underlying integer,
        // so a name-based comparison would otherwise be parsed as a number and always fail.
        if (propertyType.IsEnum)
        {
            // By name first ("Approved"), then by underlying ordinal ("2"). Studio writes the
            // name; a hand-edited or serialised definition may carry the number.
            if (Enum.TryParse(propertyType, expectedValue, true, out var enumValue) && enumValue is not null)
            {
                return CompareEquality(Equals(propertyValue, enumValue), op);
            }

            if (decimal.TryParse(expectedValue, NumberStyles.Any, CultureInfo.InvariantCulture, out var ordinal))
            {
                var actualOrdinal = Convert.ToDecimal(propertyValue, CultureInfo.InvariantCulture);
                return CompareDecimals(actualOrdinal, ordinal, op);
            }

            return false;
        }

        // Handle date properties.
        //
        // Dates are neither numeric nor orderable as strings, so they used to fall through to the
        // string branch where "lessthan" is unsupported and silently returned false. Every
        // deadline, escalation or expiry condition therefore blocked its own transition.
        if (propertyValue is DateTime or DateTimeOffset)
        {
            if (!DateTimeOffset.TryParse(
                    expectedValue,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var expectedDate))
            {
                return false;
            }

            var actualDate = propertyValue switch
            {
                DateTime dateTime => new DateTimeOffset(
                    dateTime.Kind == DateTimeKind.Unspecified
                        ? DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)
                        : dateTime.ToUniversalTime()),
                DateTimeOffset offset => offset.ToUniversalTime(),
                _ => default
            };

            return CompareComparable(actualDate, expectedDate, op);
        }

        // Handle numeric properties
        if (IsNumericType(propertyType))
        {
            // Converted directly rather than round-tripped through ToString(). The value is already
            // a number; formatting it first used the ambient culture, so under a locale with a
            // comma decimal separator 100.50m became "100,50" and then failed to parse as
            // invariant -- the condition returned false purely because of the server's locale.
            decimal currentValue;
            try
            {
                currentValue = Convert.ToDecimal(propertyValue, CultureInfo.InvariantCulture);
            }
            catch (OverflowException)
            {
                return false;
            }

            if (!decimal.TryParse(expectedValue, NumberStyles.Any, CultureInfo.InvariantCulture, out var expectedDecimal))
                return false;

            return CompareDecimals(currentValue, expectedDecimal, op);
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

    /// <summary>
    /// Applies an equality-only operator to an already-computed equality result.
    /// </summary>
    private static bool CompareEquality(bool areEqual, string op) => op.ToLowerInvariant() switch
    {
        "equal" or "==" or "equals" => areEqual,
        "notequal" or "!=" or "notequals" => !areEqual,
        _ => false
    };

    /// <summary>
    /// Applies a comparison operator to two decimals.
    /// </summary>
    private static bool CompareDecimals(decimal actual, decimal expected, string op) =>
        CompareComparable(actual, expected, op);

    /// <summary>
    /// Applies a comparison operator to any two comparable values of the same type.
    /// </summary>
    /// <remarks>
    /// Shared by the numeric, enum-ordinal and date branches so the operator vocabulary cannot
    /// diverge between them, which is how ordering came to be supported for numbers and silently
    /// unsupported for dates.
    /// </remarks>
    private static bool CompareComparable<T>(T actual, T expected, string op) where T : IComparable<T>
    {
        var comparison = actual.CompareTo(expected);

        return op.ToLowerInvariant() switch
        {
            "equal" or "==" or "equals" => comparison == 0,
            "notequal" or "!=" or "notequals" => comparison != 0,
            "lessthan" or "<" => comparison < 0,
            "lessthanorequal" or "<=" => comparison <= 0,
            "greaterthan" or ">" => comparison > 0,
            "greaterthanorequal" or ">=" => comparison >= 0,
            _ => false
        };
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
