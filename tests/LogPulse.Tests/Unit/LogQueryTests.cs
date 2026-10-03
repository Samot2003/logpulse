using LogPulse.Core.Queries;

namespace LogPulse.Tests.Unit;

public class LogQueryTests
{
    [Theory]
    [InlineData(0, 0, 1, 1)]
    [InlineData(-5, 10_000, 1, LogQuery.MaxPageSize)]
    [InlineData(3, 25, 3, 25)]
    public void Normalized_clamps_page_and_page_size(int page, int pageSize, int expectedPage, int expectedPageSize)
    {
        var normalized = new LogQuery { Page = page, PageSize = pageSize }.Normalized();

        Assert.Equal(expectedPage, normalized.Page);
        Assert.Equal(expectedPageSize, normalized.PageSize);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("   ", null)]
    [InlineData("  timeout ", "timeout")]
    public void Normalized_trims_search_and_drops_blank(string? search, string? expected)
    {
        Assert.Equal(expected, new LogQuery { Search = search }.Normalized().Search);
    }

    [Fact]
    public void Normalized_never_shortens_a_long_search()
    {
        var search = new string('x', 1_000);

        Assert.Equal(search, new LogQuery { Search = search }.Normalized().Search);
    }

    [Theory]
    [InlineData(1, 50, 0L)]
    [InlineData(3, 25, 50L)]
    [InlineData(0, 0, 0L)]
    [InlineData(int.MaxValue, LogQuery.MaxPageSize, (int.MaxValue - 1L) * LogQuery.MaxPageSize)]
    public void Offset_is_computed_in_long_without_overflow(int page, int pageSize, long expected)
    {
        Assert.Equal(expected, new LogQuery { Page = page, PageSize = pageSize }.Offset);
    }

    [Theory]
    [InlineData(0, 50, 0)]
    [InlineData(1, 50, 1)]
    [InlineData(101, 50, 3)]
    public void PagedResult_computes_total_pages(long total, int pageSize, int expectedPages)
    {
        Assert.Equal(expectedPages, new PagedResult<int>([], 1, pageSize, total).TotalPages);
    }
}
