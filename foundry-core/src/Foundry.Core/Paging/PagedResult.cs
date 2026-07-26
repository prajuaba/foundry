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
    /// <remarks>
    /// Guards a non-positive page size. Dividing by zero in floating point yields Infinity, which
    /// saturates to <see cref="long.MaxValue"/> when cast, and a negative size produced a negative
    /// page count. A client looping while <c>PageNumber &lt; TotalPages</c> therefore either
    /// paginated almost indefinitely or stopped after the first page, with nothing reporting a
    /// problem. <see cref="PagedRequest"/> defaults to 20 but nothing stops a caller binding 0 from
    /// a query string.
    /// </remarks>
    public long TotalPages => TotalRecords > 0 && PageSize > 0
        ? (long)Math.Ceiling((double)TotalRecords / PageSize)
        : 0L;

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
    /// <remarks>
    /// Seek pagination reads one row beyond the page as a "is there more?" sentinel. The sentinel is
    /// trimmed here rather than returned: previously a caller that passed the untrimmed list got
    /// back <c>PageSize + 1</c> items, so the first record of the next page appeared twice --
    /// once at the end of this page and again at the start of the next.
    /// </remarks>
    public static PagedResult<T> WithCursor(IReadOnlyList<T> items, long totalCountOrOneMoreThanTotal,
        int pageNumber, int pageSize, CursorSeekInfo nextCursor) => new()
    {
        Items = pageSize > 0 && items.Count > pageSize ? [.. items.Take(pageSize)] : [.. items],
        TotalRecords = items.Count > pageSize ? totalCountOrOneMoreThanTotal - 1 : (long)items.Count,
        PageNumber = pageNumber,
        PageSize = pageSize,
        NextCursor = nextCursor,
        HasNextPage = items.Count >= pageSize,
    };

    /// <summary>
    /// Converts an item type to another type using projection. Useful for mapping entity -> DTO in pagination results.
    /// </summary>
    /// <remarks>
    /// Carries <see cref="NextCursor"/> and <see cref="HasNextPage"/> across the projection.
    /// <para>
    /// Both used to be dropped, which matters most in the case this method exists for: mapping
    /// entities to DTOs for an API response. In cursor mode <c>TotalRecords</c> is only the size of
    /// the current page, so a mapped result recomputed <c>HasNextPage</c> as
    /// <c>PageNumber &lt; TotalPages</c> — that is, <c>1 &lt; 1</c> — and reported itself as the
    /// last page while also losing the cursor needed to ask for the next one. A client paging
    /// through mapped results silently received only the first page of the collection.
    /// </para>
    /// </remarks>
    public PagedResult<TResult> Map<TResult>(Func<T, TResult> selector) where TResult : class => new()
    {
        Items = Items.Select(selector).ToList(),
        TotalRecords = TotalRecords,
        PageNumber = PageNumber,
        PageSize = PageSize,
        NextCursor = NextCursor,
        HasNextPage = HasNextPage,
    };
}
