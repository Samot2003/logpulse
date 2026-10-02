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

    [Theory]
    [InlineData(0, 50, 0)]
    [InlineData(1, 50, 1)]
    [InlineData(101, 50, 3)]
    public void PagedResult_computes_total_pages(long total, int pageSize, int expectedPages)
    {
        Assert.Equal(expectedPages, new PagedResult<int>([], 1, pageSize, total).TotalPages);
    }
}
