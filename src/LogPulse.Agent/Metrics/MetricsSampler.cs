using LogPulse.Agent.Options;
using LogPulse.Agent.Shipping;
using LogPulse.Core.Contracts;
using Microsoft.Extensions.Options;

namespace LogPulse.Agent.Metrics;

/// <summary>
/// Takes a metric sample every <see cref="AgentOptions.MetricsInterval"/> while someone watches the dashboard and
/// every <see cref="AgentOptions.IdleMetricsInterval"/> while nobody does.
/// </summary>
public sealed partial class MetricsSampler(
    IMetricsCollector collector,
    AgentBuffers buffers,
    ViewerActivity viewers,
    IOptions<AgentOptions> options,
    TimeProvider time,
    ILogger<MetricsSampler> logger) : BackgroundService
{
    private bool _failing;
    private bool _diskFailing;
    private double? _lastDisk;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        var diskPath = settings.DiskPath is { Length: > 0 } path ? path : DiskUsage.DefaultPath;
        long? lastSample = null;

        // Ticks at the active rate even while idle, so a viewer arriving gets full-rate data within one tick.
        using var timer = new PeriodicTimer(settings.MetricsInterval, time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (!ShouldSample(viewers.ViewersOnline, lastSample is { } last ? time.GetElapsedTime(last) : null, settings))
            {
                continue;
            }

            lastSample = time.GetTimestamp();
            try
            {
                var system = collector.Collect();
                // DropOldest: never blocks, even if the API has been unreachable for hours.
                buffers.Metrics.Writer.TryWrite(new IngestMetricSample
                {
                    Timestamp = time.GetUtcNow(),
                    CpuPercent = system.CpuPercent,
                    MemoryUsedMb = system.MemoryUsedMb,
                    MemoryTotalMb = system.MemoryTotalMb,
                    DiskUsedPercent = ReadDisk(diskPath),
                });
                _failing = false;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A failed reading must not stop the agent; the next tick retries. Logged once per failure streak.
                if (!_failing)
                {
                    LogSampleFailed(logger, ex);
                    _failing = true;
                }
            }
        }
    }

    // A disk that cannot be read (unmounted, not ready) must not cost the CPU and memory values: the last known
    // disk usage is sent instead. Without any, the whole sample fails.
    private double ReadDisk(string path)
    {
        try
        {
            _lastDisk = DiskUsage.UsedPercent(path);
            _diskFailing = false;
        }
        catch (Exception ex) when (_lastDisk is not null && ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            if (!_diskFailing)
            {
                LogDiskFailed(logger, path, ex);
                _diskFailing = true;
            }
        }

        return _lastDisk ?? 0;
    }

    /// <summary>
    /// Called on every tick (every <see cref="AgentOptions.MetricsInterval"/>). While nobody watches, only once
    /// <see cref="AgentOptions.IdleMetricsInterval"/> has passed, with half a tick of tolerance for timer jitter.
    /// </summary>
    public static bool ShouldSample(bool viewersOnline, TimeSpan? sinceLastSample, AgentOptions settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return viewersOnline
            || sinceLastSample is not { } elapsed
            || elapsed + (settings.MetricsInterval / 2) >= settings.IdleMetricsInterval;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read the system metrics; retrying on every tick")]
    private static partial void LogSampleFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read the disk usage of {Path}; sending the last known value")]
    private static partial void LogDiskFailed(ILogger logger, string path, Exception exception);
}
