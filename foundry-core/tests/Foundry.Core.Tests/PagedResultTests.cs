using Foundry.Core.Paging;
using Xunit;

namespace Foundry.Core.Tests;

/// <summary>
/// Pagination metadata in <see cref="PagedResult{T}"/>.
/// </summary>
/// <remarks>
/// Clients drive their paging loops from <c>TotalPages</c>, <c>HasNextPage</c> and
/// <c>NextCursor</c>. A wrong value here does not throw — it makes a caller stop early and silently
/// miss records, or loop forever. Both are worse than an error.
/// </remarks>
public class PagedResultTests
{
    private static PagedResult<string> Page(int number, int size, long total) =>
        PagedResult<string>.From(["a", "b"], total, number, size);

    // ---- page arithmetic ----

    [Theory]
    [InlineData(100, 10, 10)]
    [InlineData(101, 10, 11)]   // partial last page counts
    [InlineData(9, 10, 1)]
    [InlineData(10, 10, 1)]
    [InlineData(0, 10, 0)]
    public void TotalPages_RoundsUp(long totalRecords, int pageSize, long expected)
    {
        Assert.Equal(expected, Page(1, pageSize, totalRecords).TotalPages);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void TotalPages_WithNonPositivePageSize_IsZeroRatherThanNonsense(int pageSize)
    {
        // Dividing by PageSize produced Infinity for a size of 0, which saturates to long.MaxValue,
        // and -100 for a size of -1. A client looping while PageNumber < TotalPages either paginates
        // essentially forever or stops immediately, and neither case reports an error.
        var result = Page(1, pageSize, 100);

        Assert.Equal(0L, result.TotalPages);
        Assert.False(result.HasNextPage);
    }

    // ---- navigation flags ----

    [Fact]
    public void HasNextPage_IsTrueWhileEarlierThanTheLastPage()
    {
        Assert.True(Page(1, 10, 100).HasNextPage);
        Assert.True(Page(9, 10, 100).HasNextPage);
    }

    [Fact]
    public void HasNextPage_IsFalseOnTheLastPage()
    {
        Assert.False(Page(10, 10, 100).HasNextPage);
    }

    [Fact]
    public void HasPreviousPage_ReflectsA1BasedPageNumber()
    {
        Assert.False(Page(1, 10, 100).HasPreviousPage);
        Assert.True(Page(2, 10, 100).HasPreviousPage);
    }

    [Fact]
    public void Empty_ReportsNoPagesAndNoNavigation()
    {
        var empty = PagedResult<string>.Empty(1, 20);

        Assert.Empty(empty.Items);
        Assert.Equal(0L, empty.TotalRecords);
        Assert.Equal(0L, empty.TotalPages);
        Assert.False(empty.HasNextPage);
        Assert.False(empty.HasPreviousPage);
    }

    // ---- cursor mode ----

    [Fact]
    public void WithCursor_CarriesTheCursorAndFlagsAFurtherPage()
    {
        var cursor = new CursorSeekInfo { FieldName = "CreatedAtUtc", Value = "x" };
        var result = PagedResult<string>.WithCursor(["a", "b"], 3, 1, 2, cursor);

        Assert.NotNull(result.NextCursor);
        Assert.True(result.HasNextPage);
    }

    [Fact]
    public void WithCursor_NeverReturnsMoreItemsThanThePageSize()
    {
        // Seek pagination fetches one extra row as a "is there more?" sentinel. If that sentinel is
        // not trimmed, the page hands back PageSize + 1 items and the caller sees the first record
        // of the next page twice.
        var cursor = new CursorSeekInfo { FieldName = "CreatedAtUtc", Value = "x" };

        var result = PagedResult<string>.WithCursor(["a", "b", "c"], 4, 1, 2, cursor);

        Assert.True(
            result.Items.Count <= 2,
            $"page size is 2 but {result.Items.Count} items were returned");
    }

    // ---- projection ----

    [Fact]
    public void Map_PreservesPagingMetadata()
    {
        var mapped = PagedResult<string>.From(["a", "b"], 100, 2, 10).Map(s => s.ToUpperInvariant());

        Assert.Equal(2, mapped.Items.Count);
        Assert.Equal("A", mapped.Items[0]);
        Assert.Equal(100L, mapped.TotalRecords);
        Assert.Equal(2, mapped.PageNumber);
        Assert.Equal(10, mapped.PageSize);
        Assert.Equal(10L, mapped.TotalPages);
        Assert.True(mapped.HasNextPage);
    }

    [Fact]
    public void Map_PreservesTheCursor()
    {
        // Map exists to turn entities into DTOs for an API response, which is exactly where cursor
        // pagination is used. Dropping NextCursor left the client with no way to request the next
        // page, and dropping HasNextPage made a cursor page always report itself as the last one --
        // so a mapped result silently truncated the collection.
        var cursor = new CursorSeekInfo { FieldName = "CreatedAtUtc", Value = "x" };
        var page = PagedResult<string>.WithCursor(["a", "b"], 3, 1, 2, cursor);

        var mapped = page.Map(s => s.ToUpperInvariant());

        Assert.NotNull(mapped.NextCursor);
        Assert.Equal("CreatedAtUtc", mapped.NextCursor!.FieldName);
        Assert.True(mapped.HasNextPage);
    }
}
