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

        // "(none → x)", not "(no change → x)". The previous wording stated the opposite of what the
        // record contains: this branch is a value being set where there was none, which is a change.
        // An audit line reading "no change" for a change is misleading evidence, which in a
        // compliance trail is worse than a missing line.
        if (OldValue == null) return $"(none → {FormatValue(NewValue)})";

        if (NewValue == null) return $"{FormatValue(OldValue)} (removed)";
        var oldStr = OldValue.ToString() ?? string.Empty;
        var newStr = NewValue.ToString() ?? string.Empty;
        return $"'{oldStr}' → '{newStr}'";
    }

    /// <remarks>
    /// Strings are rendered explicitly rather than falling through to the type-name arm. They used
    /// to print as <c>&lt;String&gt;</c>, so an audit line for a newly-set or removed text field
    /// named the type instead of the value — on the insert and removal branches only, while the
    /// both-values branch printed it correctly. The trail was therefore incomplete and internally
    /// inconsistent.
    /// </remarks>
    private static string FormatValue(object? value) => value switch
    {
        null => "null",
        bool v => v ? "true" : "false",
        string s => $"'{s}'",
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
