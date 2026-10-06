using System.Runtime.Versioning;

namespace LogPulse.Agent.Metrics;

[SupportedOSPlatform("linux")]
public sealed class LinuxMetricsCollector : IMetricsCollector
{
    private readonly CpuUsageCalculator _cpu = new();

    // The first reading is the baseline: the first Collect reports the usage since the agent started.
    public LinuxMetricsCollector() => _cpu.Update(ReadCpuTimes());

    public SystemMetrics Collect()
    {
        var cpu = _cpu.Update(ReadCpuTimes());
        var (usedMb, totalMb) = ProcParsers.ParseMemory(File.ReadAllText("/proc/meminfo"));
        return new SystemMetrics(cpu, usedMb, totalMb);
    }

    private static CpuTimes ReadCpuTimes() => ProcParsers.ParseCpuTimes(File.ReadAllText("/proc/stat"));
}
