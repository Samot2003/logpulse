namespace LogPulse.Agent.Metrics;

/// <summary>CPU and memory usage at one point in time.</summary>
public readonly record struct SystemMetrics(double CpuPercent, long MemoryUsedMb, long MemoryTotalMb);

/// <summary>Reads CPU and memory usage from the operating system.</summary>
public interface IMetricsCollector
{
    /// <summary>The CPU usage is the average since the previous call (or since the collector was created).</summary>
    SystemMetrics Collect();
}
