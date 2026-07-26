namespace Foundry.Core.Audit;

/// <summary>
/// Represents a single property change detected during an update operation.
/// Captures the property name with its pre-update and post-update values for immutable audit trail.
/// </summary>
public readonly record struct PropertyDiff(string PropertyName, object? OldValue, object? NewValue)
{
    /// <summary>True when the property value actually changed (comparing old vs new via Equals).</summary>
    public bool HasChanged => !object.Equals(OldValue, NewValue);

    /// <summary>Returns a human-readable diff string: "PropertyName: 'old' -> 'new'".</summary>
    public override string ToString() => $"{PropertyName}: {FormatDiff()}";

    private string FormatDiff()
    {
        if (OldValue == null && NewValue == null) return "(null → null)";
        if (OldValue == null) return $"(no change → {FormatValue(NewValue)})";
        if (NewValue == null) return $"{FormatValue(OldValue)} (removed)";
        var oldStr = OldValue.ToString() ?? string.Empty;
        var newStr = NewValue.ToString() ?? string.Empty;
        return $"'{oldStr}' → '{newStr}'";
    }

    private static string FormatValue(object? value) => value switch
    {
        null => "null",
        bool v => v ? "true" : "false",
        int or long or float or double or decimal => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
        DateTime dt => dt.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        Guid g when g == Guid.Empty => "Guid.Empty",
        Enum e => e.ToString() ?? string.Empty,
        _ => $"<{value.GetType().Name}>"
    };

    /// <summary>
    /// Creates a PropertyDiff indicating an Insert operation (no old value).
    /// </summary>
    public static PropertyDiff Inserted(string propertyName, object? newValue) =>
        new(propertyName, null, newValue);

    /// <summary>
    /// Creates a PropertyDiff indicating a field removal during update.
    /// </summary>
    public static PropertyDiff Removed(string propertyName, object? oldValue) =>
        new(propertyName, oldValue, null);
}
