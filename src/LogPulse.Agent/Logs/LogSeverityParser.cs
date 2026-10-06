using System.Text.RegularExpressions;
using LogPulse.Core.Models;

namespace LogPulse.Agent.Logs;

/// <summary>Guesses the severity of a plain-text log line from the level word most formats put near its start.</summary>
public static partial class LogSeverityParser
{
    // Only the start of the line is searched: that is where the level is, and "retrying after error" at the end
    // of an informational message must not turn it into an error.
    private const int SearchLength = 120;

    public static LogSeverity Detect(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        var match = LevelWord().Match(line, 0, Math.Min(line.Length, SearchLength));
        if (!match.Success)
        {
            return LogSeverity.Information;
        }

        // Debug and trace levels have no severity of their own in LogPulse: they are kept as Information.
        return match.Value.ToUpperInvariant() switch
        {
            "CRITICAL" or "CRIT" or "FATAL" or "FTL" or "EMERG" => LogSeverity.Critical,
            "ERROR" or "ERR" or "FAIL" => LogSeverity.Error,
            "WARNING" or "WARN" or "WRN" => LogSeverity.Warning,
            _ => LogSeverity.Information,
        };
    }

    // Whole words only, so "errors=0" or "Informational" do not match. Covers the usual spellings: log4net/NLog
    // (ERROR, WARN), Serilog ([ERR], [WRN]), the .NET console logger (fail:, warn:, crit:) and syslog (emerg).
    [GeneratedRegex(
        @"\b(CRITICAL|CRIT|FATAL|FTL|EMERG|ERROR|ERR|FAIL|WARNING|WARN|WRN|INFORMATION|INFO|INF|DEBUG|DBUG|DBG|TRACE|TRCE|TRC|VERBOSE|VRB)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LevelWord();
}
