namespace Foundry.Core.Paging;

/// <summary>
/// Defines seek pagination metadata for O(1) cursor-based navigation on large collections.
/// </summary>
public sealed record CursorSeekInfo
{
    /// <summary>Name of the field to use as the sort key (e.g., "Id", "CreatedAt").</summary>
    public required string FieldName { get; init; }

    /// <summary>The last-seen value for this field — null means no cursor yet (first page).</summary>
    public required object? Value { get; init; }

    /// <summary>Sort direction of the pagination result set.</summary>
    public SortOrder Order { get; init; } = SortOrder.Ascending;

    /// <summary>
    /// Creates a cursor seek info indicating 'no cursor' (first page, full scan).
    /// </summary>
    public static CursorSeekInfo FirstPage(string fieldName, SortOrder order = SortOrder.Ascending) =>
        new() { FieldName = fieldName, Value = null as object, Order = order };

    /// <summary>
    /// Creates a cursor seek info for continuation from a specific entity.
    /// </summary>
    public static CursorSeekInfo FromValue<T>(T entity, string fieldName, SortOrder order) where T : class
    {
        var propInfo = typeof(T).GetProperty(fieldName);
        if (propInfo == null || !propInfo.CanRead)
            throw new ArgumentException($"Property '{fieldName}' not found on type '{typeof(T).Name}'", nameof(fieldName));

        return new() { FieldName = fieldName, Value = propInfo.GetValue(entity), Order = order };
    }
}

/// <summary>Represents the sort direction in pagination queries.</summary>
public enum SortOrder : byte { Ascending, Descending }
