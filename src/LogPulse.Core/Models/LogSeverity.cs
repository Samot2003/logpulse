namespace LogPulse.Core.Models;

/// <summary>Severity of a log entry. Stored as TINYINT; order matters for minimum-severity filters.</summary>
public enum LogSeverity : byte
{
    Information = 0,
    Warning = 1,
    Error = 2,
    Critical = 3,
}
