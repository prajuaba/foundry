namespace Foundry.Core.Paging;

/// <summary>
/// Defines a pagination request that can be used with either offset or cursor-based navigation.
/// MaxDepthCap prevents performance degradation on deep page requests.
/// </summary>
public sealed record PagedRequest
{
    /// <summary>1-based page number to return.</summary>
    public int PageNumber { get; init; } = 1;

    /// <summary>Number of items per page.</summary>
    public int PageSize { get; init; } = 20;

    /// <summary>Maximum allowed depth for offset pagination. Prevents deep scans on huge collections.</summary>
    public int MaxDepthCap { get; init; } = 10_000;

    /// <summary>Cursor-based seek info for O(1) navigation. Null for offset-based pagination.</summary>
    public CursorSeekInfo? CursorInfo { get; init; }

    /// <summary>Sorting instructions applied before pagination (field name and order).</summary>
    public SortRequest? SortBy { get; init; }

    /// <summary>True when cursor info is provided, indicating seek-based pagination.</summary>
    public bool IsCursor => CursorInfo != null;
}

/// <summary>Describes a single sort instruction for pagination queries.</summary>
public sealed record SortRequest
{
    /// <summary>The field name to sort by (supports dot-notation for nested properties).</summary>
    public required string FieldName { get; init; }

    /// <summary>Sort direction: ascending or descending.</summary>
    public SortOrder Order { get; init; } = SortOrder.Ascending;
}
