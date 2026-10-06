using System.Runtime.Versioning;
using LogPulse.Agent.Metrics;
using LogPulse.Agent.Options;
using LogPulse.Tests.Fakes;

namespace LogPulse.Tests.Unit.Agent;

public class MetricsTests
{
    private const string ProcStat = """
        cpu  10132153 290696 3084719 46828483 16683 0 25195 0 175628 0
        cpu0 1393280 32966 572056 13343292 6130 0 17875 0 23933 0
        intr 114930548 113199788 3 0 5 263 0 4
        ctxt 1990473
        """;

    private const string MemInfo = """
        MemTotal:       16314020 kB
        MemFree:         1245220 kB
        MemAvailable:    9876544 kB
        Buffers:          345678 kB
        Cached:          7654321 kB
        SwapTotal:       2097148 kB
        """;

    [Fact]
    public void Cpu_line_sums_the_first_eight_fields_and_counts_idle_plus_iowait_as_idle()
    {
        var times = ProcParsers.ParseCpuTimes(ProcStat);

        // Guest time (175628) is already part of user time and must not be added twice.
        Assert.Equal(60_377_929UL, times.Total);
        Assert.Equal(46_845_166UL, times.Idle);
    }

    [Fact]
    public void Proc_stat_without_an_aggregate_cpu_line_is_a_format_error()
    {
        Assert.Throws<FormatException>(() => ProcParsers.ParseCpuTimes("cpu0 1 2 3 4\nintr 5"));
    }

    [Fact]
    public void Used_memory_is_total_minus_available()
    {
        var (usedMb, totalMb) = ProcParsers.ParseMemory(MemInfo);

        Assert.Equal((16_314_020 - 9_876_544) / 1024, usedMb);
        Assert.Equal(16_314_020 / 1024, totalMb);
    }

    [Fact]
    public void Old_kernels_without_MemAvailable_use_free_plus_buffers_plus_cached()
    {
        var withoutAvailable = string.Join('\n', MemInfo.Split('\n').Where(l => !l.StartsWith("MemAvailable", StringComparison.Ordinal)));

        var (usedMb, _) = ProcParsers.ParseMemory(withoutAvailable);

        Assert.Equal((16_314_020 - (1_245_220 + 345_678 + 7_654_321)) / 1024, usedMb);
    }

    [Fact]
    public void Cpu_usage_is_the_busy_share_of_the_interval_between_readings()
    {
        var cpu = new CpuUsageCalculator();

        Assert.Equal(0, cpu.Update(new CpuTimes(Idle: 100, Total: 1000))); // first reading: no interval yet
        Assert.Equal(75, cpu.Update(new CpuTimes(Idle: 150, Total: 1200))); // 200 ticks, 50 idle
        Assert.Equal(0, cpu.Update(new CpuTimes(Idle: 10, Total: 100))); // counters reset: no garbage value
    }

    [Theory]
    [InlineData(100, 30, 25, 73.7)] // 5 bytes reserved for root: free but not available
    [InlineData(100, 40, 40, 60)]
    [InlineData(0, 0, 0, 0)]
    public void Disk_usage_uses_the_same_formula_as_df(long total, long free, long available, double expected)
    {
        Assert.Equal(expected, DiskUsage.Percent(total, free, available));
    }

    [Theory]
    [InlineData(true, null, true)]
    [InlineData(true, 5, true)] // someone watching: every tick
    [InlineData(false, null, true)] // the first sample is always taken
    [InlineData(false, 5, false)]
    [InlineData(false, 25, false)]
    [InlineData(false, 28, true)] // within half a tick of the idle interval (timer jitter)
    [InlineData(false, 30, true)]
    public void While_nobody_watches_samples_are_taken_at_the_idle_interval(bool viewersOnline, int? secondsSinceLast, bool expected)
    {
        var settings = new AgentOptions { MetricsInterval = TimeSpan.FromSeconds(5), IdleMetricsInterval = TimeSpan.FromSeconds(30) };
        TimeSpan? sinceLast = secondsSinceLast is { } s ? TimeSpan.FromSeconds(s) : null;

        Assert.Equal(expected, MetricsSampler.ShouldSample(viewersOnline, sinceLast, settings));
    }

    [WindowsFact]
    [SupportedOSPlatform("windows")]
    public void Windows_collector_reads_sane_values()
    {
        var metrics = new WindowsMetricsCollector().Collect();

        AssertSane(metrics);
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void Linux_collector_reads_sane_values()
    {
        var metrics = new LinuxMetricsCollector().Collect();

        AssertSane(metrics);
    }

    private static void AssertSane(SystemMetrics metrics)
    {
        Assert.InRange(metrics.CpuPercent, 0, 100);
        Assert.True(metrics.MemoryTotalMb > 0);
        Assert.InRange(metrics.MemoryUsedMb, 1, metrics.MemoryTotalMb);
        Assert.InRange(DiskUsage.UsedPercent(DiskUsage.DefaultPath), 0.1, 100);
    }
}
