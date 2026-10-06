using System.Globalization;

namespace LogPulse.Agent.Metrics;

/// <summary>Parsers for the Linux /proc files the agent reads. Pure functions, so they are tested with samples.</summary>
public static class ProcParsers
{
    /// <summary>Reads the aggregate "cpu" line of /proc/stat (times in USER_HZ ticks).</summary>
    public static CpuTimes ParseCpuTimes(string procStat)
    {
        ArgumentNullException.ThrowIfNull(procStat);

        var line = procStat.Split('\n').FirstOrDefault(l => l.StartsWith("cpu ", StringComparison.Ordinal))
            ?? throw new FormatException("/proc/stat has no aggregate cpu line.");

        // cpu user nice system idle iowait irq softirq steal [guest guest_nice]
        // Guest time is already included in user and nice, so it is not added again.
        var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 5)
        {
            throw new FormatException("/proc/stat cpu line has too few fields.");
        }

        ulong total = 0, idle = 0;
        for (var i = 1; i < Math.Min(fields.Length, 9); i++)
        {
            var value = ulong.Parse(fields[i], NumberStyles.None, CultureInfo.InvariantCulture);
            total += value;
            if (i is 4 or 5) // idle and iowait
            {
                idle += value;
            }
        }

        return new CpuTimes(idle, total);
    }

    /// <summary>
    /// Reads /proc/meminfo. Used memory is total minus available, like the "used" column of free(1): page cache
    /// that the kernel can reclaim does not count as used.
    /// </summary>
    public static (long UsedMb, long TotalMb) ParseMemory(string meminfo)
    {
        ArgumentNullException.ThrowIfNull(meminfo);

        var values = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var line in meminfo.Split('\n'))
        {
            // "MemTotal:       16314020 kB"
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue;
            }

            var text = line[(colon + 1)..].Trim();
            if (text.EndsWith(" kB", StringComparison.Ordinal))
            {
                text = text[..^3].TrimEnd();
            }

            if (long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var kilobytes))
            {
                values[line[..colon]] = kilobytes;
            }
        }

        if (!values.TryGetValue("MemTotal", out var total))
        {
            throw new FormatException("/proc/meminfo has no MemTotal.");
        }

        // MemAvailable exists since Linux 3.14; older kernels get the classic estimate.
        var available = values.TryGetValue("MemAvailable", out var reported)
            ? reported
            : values.GetValueOrDefault("MemFree") + values.GetValueOrDefault("Buffers") + values.GetValueOrDefault("Cached");

        return ((total - Math.Min(available, total)) / 1024, total / 1024);
    }
}
