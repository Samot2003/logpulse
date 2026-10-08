using Dapper;
using LogPulse.Data;
using Microsoft.Data.SqlClient;

namespace LogPulse.Tests.Integration;

/// <summary>The real errors SQL Server raises, classified the way the API's startup retries need.</summary>
[Collection(SqlServerGroup.Name)]
[Trait("Category", "Integration")]
public class SqlStartupErrorsIntegrationTests(SqlServerFixture fixture)
{
    [Fact]
    public async Task A_server_that_does_not_answer_is_worth_waiting_for()
    {
        // A port nobody listens on, with a short timeout: what the API sees before SQL Server is up.
        var unreachable = new SqlConnectionStringBuilder(fixture.ConnectionString) { DataSource = "127.0.0.1,1", ConnectTimeout = 2 };
        await using var connection = new SqlConnection(unreachable.ConnectionString);

        var error = await Assert.ThrowsAsync<SqlException>(() => connection.OpenAsync());

        Assert.True(SqlStartupErrors.IsServerNotReady(error));
    }

    [Fact]
    public async Task A_broken_script_is_not()
    {
        await using var connection = new SqlConnection(fixture.ConnectionString);

        var error = await Assert.ThrowsAsync<SqlException>(() => connection.ExecuteAsync("SELEC 1"));

        Assert.False(SqlStartupErrors.IsServerNotReady(error));
    }
}
