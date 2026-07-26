namespace Foundry.Core.Paging;

/// <summary>
/// Generic result wrapper for paginated queries. Contains items and metadata about the collection size.
/// </summary>
public class PagedResult<T>
{
    /// <summary>The items on this page.</summary>
    public IReadOnlyList<T> Items { get; init; } = [];

    /// <summary>Total number of records matching the query (ignoring pagination).</summary>
    public long TotalRecords { get; init; }

    /// <summary>1-based page number returned.</summary>
    public int PageNumber { get; init; }

    /// <summary>Requested page size.</summary>
    public int PageSize { get; init; }

    /// <summary>Total number of pages available based on TotalRecords / PageSize.</summary>
    public long TotalPages => TotalRecords > 0 ? (long)Math.Ceiling((double)TotalRecords / PageSize) : 0L;

    private readonly bool? _hasNextPage;
    /// <summary>True when there are more pages after the current one.</summary>
    public bool HasNextPage
    {
        get => _hasNextPage ?? (PageNumber < TotalPages);
        init => _hasNextPage = value;
    }

    /// <summary>True when there are pages before the current one (page > 1).</summary>
    public bool HasPreviousPage => PageNumber > 1;

    /// <summary>Cursor info for seek pagination, populated when IsCursor is true.</summary>
    public CursorSeekInfo? NextCursor { get; init; }

    /// <summary>Returns the index of the last item in this page (for cursor continuation).</summary>
    public int LastItemIndex => Items.Count > 0 ? PageNumber * PageSize - (PageSize - Items.Count) : -1;

    /// <summary>
    /// Static factory for creating empty paged results when no items match.
    /// </summary>
    public static PagedResult<T> Empty(int pageNumber, int pageSize) => new()
    {
        TotalRecords = 0L,
        PageNumber = pageNumber,
        PageSize = pageSize,
    };

    /// <summary>
    /// Static factory for creating a populated paged result from count + items.
    /// </summary>
    public static PagedResult<T> From(IReadOnlyList<T> items, long totalRecords, int pageNumber, int pageSize) => new()
    {
        Items = [.. items],
        TotalRecords = totalRecords,
        PageNumber = pageNumber,
        PageSize = pageSize,
    };

    /// <summary>
    /// Static factory for cursor-based results with next-page cursor info.
    /// </summary>
    public static PagedResult<T> WithCursor(IReadOnlyList<T> items, long totalCountOrOneMoreThanTotal, 
        int pageNumber, int pageSize, CursorSeekInfo nextCursor) => new()
    {
        Items = [.. items],
        TotalRecords = items.Count > pageSize ? totalCountOrOneMoreThanTotal - 1 : (long)items.Count,
        PageNumber = pageNumber,
        PageSize = pageSize,
        NextCursor = nextCursor,
        HasNextPage = items.Count >= pageSize,
    };

    /// <summary>
    /// Converts an item type to another type using projection. Useful for mapping entity -> DTO in pagination results.
    /// </summary>
    public PagedResult<TResult> Map<TResult>(Func<T, TResult> selector) where TResult : class => new()
    {
        Items = Items.Select(selector).ToList(),
        TotalRecords = TotalRecords,
        PageNumber = PageNumber,
        PageSize = PageSize,
    };
}
