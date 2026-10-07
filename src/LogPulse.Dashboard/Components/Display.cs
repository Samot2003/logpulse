using System.Globalization;
using LogPulse.Core.Models;

namespace LogPulse.Dashboard.Components;

/// <summary>Formatting shared by the pages. Times are shown in UTC, like the API stores them.</summary>
public static class Display
{
    public static string Ago(DateTimeOffset then, DateTimeOffset now)
    {
        var elapsed = now - then;
        return elapsed.TotalSeconds switch
        {
            < 5 => "just now",
            < 60 => $"{(int)elapsed.TotalSeconds} s ago",
            < 3600 => $"{(int)elapsed.TotalMinutes} min ago",
            < 86_400 => $"{(int)elapsed.TotalHours} h ago",
            _ => $"{(int)elapsed.TotalDays} d ago",
        };
    }

    public static string Time(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    public static string Percent(double value) => value.ToString("0.#", CultureInfo.InvariantCulture) + " %";

    public static string Gigabytes(long megabytes) =>
        (megabytes / 1024d).ToString("0.0", CultureInfo.InvariantCulture) + " GB";

    /// <summary>Memory in use as a percentage, or null when the agent did not report a total.</summary>
    public static double? MemoryPercent(MetricSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        return sample.MemoryTotalMb > 0 ? Math.Clamp(100d * sample.MemoryUsedMb / sample.MemoryTotalMb, 0, 100) : null;
    }

    /// <summary>CSS modifier for a usage level: calm below 70 %, warning below 90 %, critical above.</summary>
    public static string Level(double? percent) => percent switch
    {
        null => "unknown",
        < 70 => "ok",
        < 90 => "warn",
        _ => "critical",
    };

    public static string SeverityClass(LogSeverity severity) => severity.ToString().ToLowerInvariant();
}
