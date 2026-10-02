namespace LogPulse.Core.Models;

/// <summary>A point-in-time snapshot of a server's resource usage.</summary>
public sealed record MetricSample
{
    public long Id { get; init; }
    public int ServerId { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public double CpuPercent { get; init; }
    public long MemoryUsedMb { get; init; }
    public long MemoryTotalMb { get; init; }
    public double DiskUsedPercent { get; init; }
}
