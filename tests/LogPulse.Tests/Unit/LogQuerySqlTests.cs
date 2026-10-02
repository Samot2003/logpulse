using LogPulse.Core.Models;
using LogPulse.Core.Queries;
using LogPulse.Data.Daos;

namespace LogPulse.Tests.Unit;

public class LogQuerySqlTests
{
    [Fact]
    public void No_filters_produces_empty_where()
    {
        var (where, parameters) = LogQuerySql.BuildWhere(new LogQuery());

        Assert.Equal(string.Empty, where);
        Assert.Empty(parameters.ParameterNames);
    }

    [Fact]
    public void All_filters_are_parameterized_and_combined_with_and()
    {
        var query = new LogQuery
        {
            ServerId = 7,
            MinSeverity = LogSeverity.Error,
            From = DateTimeOffset.UnixEpoch,
            To = DateTimeOffset.UnixEpoch.AddDays(1),
            Search = "disk",
        };

        var (where, parameters) = LogQuerySql.BuildWhere(query);

        Assert.StartsWith("WHERE ", where, StringComparison.Ordinal);
        Assert.Equal(4, where.Split(" AND ").Length - 1);
        Assert.Equal(["ServerId", "MinSeverity", "From", "To", "Search"], parameters.ParameterNames);
        Assert.Equal((byte)LogSeverity.Error, parameters.Get<byte>("MinSeverity"));
        Assert.Equal("%disk%", parameters.Get<string>("Search"));
    }

    [Theory]
    [InlineData("100%", @"100\%")]
    [InlineData("a_b", @"a\_b")]
    [InlineData("[x]", @"\[x]")]
    [InlineData(@"C:\logs", @"C:\\logs")]
    [InlineData("plain", "plain")]
    public void EscapeLike_escapes_wildcards(string input, string expected)
    {
        Assert.Equal(expected, LogQuerySql.EscapeLike(input));
    }
}
