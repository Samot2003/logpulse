using System.Globalization;
using System.Text;

namespace LogPulse.Dashboard.Components.Charts;

public readonly record struct ChartPoint(DateTimeOffset Time, double Value);

/// <summary>Turns samples into SVG polylines. Pure functions, so the chart's maths is tested without rendering.</summary>
public static class ChartGeometry
{
    /// <summary>
    /// Polylines (SVG "points" attributes) for the samples between <paramref name="from"/> and <paramref name="to"/>.
    /// Samples are averaged per horizontal pixel, so a day of 5-second samples stays a few hundred points; and a
    /// pause longer than <paramref name="gap"/> (the agent was offline) starts a new line instead of drawing a
    /// straight segment over the missing data.
    /// </summary>
    public static IReadOnlyList<string> Lines(
        IEnumerable<ChartPoint> points, DateTimeOffset from, DateTimeOffset to, double max, double width, double height, TimeSpan gap)
    {
        ArgumentNullException.ThrowIfNull(points);
        var span = (to - from).TotalMilliseconds;
        if (span <= 0 || max <= 0 || width <= 0 || height <= 0)
        {
            return [];
        }

        // One bucket per pixel column, with the times of its first and last samples.
        var buckets = new List<(int Column, double Sum, int Count, DateTimeOffset First, DateTimeOffset Last)>();
        foreach (var point in points.Where(p => p.Time >= from && p.Time <= to && double.IsFinite(p.Value)).OrderBy(p => p.Time))
        {
            var column = (int)Math.Round((point.Time - from).TotalMilliseconds / span * width);
            if (buckets.Count > 0 && buckets[^1].Column == column && point.Time - buckets[^1].Last <= gap)
            {
                var last = buckets[^1];
                buckets[^1] = (column, last.Sum + point.Value, last.Count + 1, last.First, point.Time);
            }
            else
            {
                buckets.Add((column, point.Value, 1, point.Time, point.Time));
            }
        }

        var lines = new List<string>();
        var current = new StringBuilder();
        DateTimeOffset? previousLast = null;
        foreach (var (column, sum, count, first, last) in buckets)
        {
            // The gap is measured between consecutive samples, not between buckets: a pixel can span minutes.
            if (previousLast is { } before && first - before > gap && current.Length > 0)
            {
                lines.Add(current.ToString());
                current.Clear();
            }

            var value = Math.Clamp(sum / count, 0, max);
            if (current.Length > 0)
            {
                current.Append(' ');
            }

            current.Append(Format(column)).Append(',').Append(Format(height - (value / max * height)));
            previousLast = last;
        }

        if (current.Length > 0)
        {
            lines.Add(current.ToString());
        }

        return lines;
    }

    private static string Format(double value) => Math.Round(value, 1).ToString(CultureInfo.InvariantCulture);
}
