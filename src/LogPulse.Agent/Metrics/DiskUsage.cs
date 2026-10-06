namespace LogPulse.Agent.Metrics;

public static class DiskUsage
{
    /// <summary>The system drive on Windows (usually C:\), the root file system on Linux.</summary>
    public static string DefaultPath =>
        OperatingSystem.IsWindows() ? Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\" : "/";

    /// <summary>Used space of the drive or mount point at <paramref name="path"/>, as a percentage.</summary>
    public static double UsedPercent(string path)
    {
        var drive = new DriveInfo(path);
        return Percent(drive.TotalSize, drive.TotalFreeSpace, drive.AvailableFreeSpace);
    }

    /// <summary>
    /// The same formula as df: used / (used + available). On Linux, blocks reserved for root are free but not
    /// available, so they count neither as used nor as usable; on Windows free and available are usually equal.
    /// </summary>
    public static double Percent(long totalBytes, long freeBytes, long availableBytes)
    {
        var used = totalBytes - freeBytes;
        var usable = used + availableBytes;
        return usable <= 0 ? 0 : Math.Round(Math.Clamp(100.0 * used / usable, 0, 100), 1);
    }
}
