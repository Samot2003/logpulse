namespace LogPulse.Agent.Metrics;

/// <summary>Cumulative CPU time counters since boot: idle time and total time, in any unit.</summary>
public readonly record struct CpuTimes(ulong Idle, ulong Total);

/// <summary>
/// Turns cumulative CPU counters into a usage percentage over the interval between two readings. Both Windows
/// (GetSystemTimes) and Linux (/proc/stat) only expose counters since boot, so usage is always a difference.
/// </summary>
public sealed class CpuUsageCalculator
{
    private CpuTimes? _previous;

    /// <summary>Records a reading and returns the usage since the previous one (0 for the first reading).</summary>
    public double Update(CpuTimes current)
    {
        var previous = _previous;
        _previous = current;

        // No interval yet, or the counters went backwards (wrapped or reset): report nothing rather than garbage.
        if (previous is not { } before || current.Total <= before.Total || current.Idle < before.Idle)
        {
            return 0;
        }

        var total = current.Total - before.Total;
        var idle = Math.Min(current.Idle - before.Idle, total);
        return Math.Round(100.0 * (total - idle) / total, 1);
    }
}
