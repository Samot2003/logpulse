using LogPulse.Api.Infrastructure;
using Microsoft.Extensions.Configuration;

namespace LogPulse.Tests.Unit;

public class DatabaseStartupTests
{
    [Theory]
    [InlineData(null, true)] // local and compose: the API creates its own database
    [InlineData("true", true)]
    [InlineData("false", false)] // Azure: the free database is provisioned beforehand and never created by the API
    public void Database_creation_can_be_turned_off(string? setting, bool expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Database:CreateIfMissing"] = setting })
            .Build();

        Assert.Equal(expected, DatabaseStartup.CreateIfMissing(configuration));
    }
}
