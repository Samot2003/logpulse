using LogPulse.Agent.Logs;
using LogPulse.Core.Models;

namespace LogPulse.Tests.Unit.Agent;

public class LogSeverityParserTests
{
    [Theory]
    [InlineData("2026-10-05 12:00:00,123 ERROR [main] Payment service timed out", LogSeverity.Error)]
    [InlineData("[12:00:00 WRN] Disk space below 15%", LogSeverity.Warning)]
    [InlineData("fail: Microsoft.AspNetCore.Server.Kestrel[13] Connection error", LogSeverity.Error)]
    [InlineData("crit: Microsoft.Hosting.Lifetime[0] Host terminated", LogSeverity.Critical)]
    [InlineData("FATAL Out of memory", LogSeverity.Critical)]
    [InlineData("Warning: certificate expires in 3 days", LogSeverity.Warning)]
    [InlineData("2026-10-05 INFO Retrying after error", LogSeverity.Information)] // the first level word wins
    [InlineData("DEBUG Cache warmed up", LogSeverity.Information)] // no debug severity in LogPulse
    [InlineData("errors=0 warnings=0 build ok", LogSeverity.Information)] // whole words only
    [InlineData("Plain line without a level", LogSeverity.Information)]
    public void Detects_the_level_word_near_the_start(string line, LogSeverity expected)
    {
        Assert.Equal(expected, LogSeverityParser.Detect(line));
    }

    [Fact]
    public void A_level_word_far_into_the_message_is_ignored()
    {
        var line = new string('x', 200) + " ERROR";

        Assert.Equal(LogSeverity.Information, LogSeverityParser.Detect(line));
    }
}
