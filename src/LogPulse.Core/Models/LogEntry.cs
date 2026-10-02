namespace LogPulse.Core.Models;

/// <summary>A single log line reported by an agent.</summary>
public sealed record LogEntry
{
    public long Id { get; init; }
    public int ServerId { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public LogSeverity Severity { get; init; }
    public required string Source { get; init; }
    public required string Message { get; init; }
    public string? Exception { get; init; }
}
