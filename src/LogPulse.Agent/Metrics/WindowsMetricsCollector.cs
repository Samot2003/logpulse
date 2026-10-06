using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace LogPulse.Agent.Metrics;

/// <summary>
/// Reads CPU and memory through the Win32 API directly: no PerformanceCounter package, and the same
/// "difference of cumulative counters" approach as on Linux.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsMetricsCollector : IMetricsCollector
{
    private const ulong BytesPerMb = 1024 * 1024;

    private readonly CpuUsageCalculator _cpu = new();

    // The first reading is the baseline: the first Collect reports the usage since the agent started.
    public WindowsMetricsCollector() => _cpu.Update(ReadCpuTimes());

    public SystemMetrics Collect()
    {
        var cpu = _cpu.Update(ReadCpuTimes());

        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref memory))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        return new SystemMetrics(
            cpu,
            (long)((memory.TotalPhys - memory.AvailPhys) / BytesPerMb),
            (long)(memory.TotalPhys / BytesPerMb));
    }

    private static CpuTimes ReadCpuTimes()
    {
        // In 100 ns units since boot. Kernel time already includes idle time.
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        return new CpuTimes((ulong)idle, (ulong)(kernel + user));
    }

    /// <summary>MEMORYSTATUSEX.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatus buffer);
}
