using System.Data.Common;
using LogPulse.Data;

namespace LogPulse.Tests.Unit;

public class SqlStartupErrorsTests
{
    [Theory]
    [InlineData(-2)] // timeout
    [InlineData(10061)] // connection refused
    [InlineData(18456)] // login failed while the container sets the sa password
    [InlineData(4060)] // database not available yet
    [InlineData(922)] // database being recovered
    public void Errors_of_a_server_still_starting_are_retried(int number) =>
        Assert.True(SqlStartupErrors.IsNotReadyNumber(number));

    [Theory]
    [InlineData(102)] // syntax error in a schema script
    [InlineData(229)] // permission denied
    [InlineData(2627)] // unique constraint violation
    public void Errors_that_waiting_cannot_fix_fail_at_once(int number) =>
        Assert.False(SqlStartupErrors.IsNotReadyNumber(number));

    [Fact]
    public void Other_providers_use_their_own_transient_flag()
    {
        Assert.True(SqlStartupErrors.IsServerNotReady(new FakeDbException(transient: true)));
        Assert.False(SqlStartupErrors.IsServerNotReady(new FakeDbException(transient: false)));
    }

    private sealed class FakeDbException(bool transient) : DbException
    {
        public override bool IsTransient => transient;
    }
}
