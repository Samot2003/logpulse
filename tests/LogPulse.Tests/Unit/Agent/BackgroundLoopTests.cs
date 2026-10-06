using LogPulse.Agent.Logs;
using LogPulse.Agent.Metrics;
using LogPulse.Agent.Options;
using LogPulse.Agent.Shipping;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging.Abstractions;

namespace LogPulse.Tests.Unit.Agent;

/// <summary>The polling loops of the log tailer and the metrics sampler, run for real with short intervals.</summary>
public sealed class BackgroundLoopTests : IDisposable
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    private readonly string _dir = Directory.CreateTempSubdirectory("logpulse-loops-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static async Task EventuallyAsync(Func<bool> condition)
    {
        var started = DateTime.UtcNow;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow - started < Deadline, "The condition was not met in time.");
            await Task.Delay(20);
        }
    }

    private static List<T> Drain<T>(System.Threading.Channels.ChannelReader<T> reader)
    {
        var items = new List<T>();
        while (reader.TryRead(out var item))
        {
            items.Add(item);
        }

        return items;
    }

    private sealed class FlakyCollector : IMetricsCollector
    {
        public int Calls { get; private set; }

        public SystemMetrics Collect() =>
            ++Calls == 1 ? throw new IOException("counter not ready") : new SystemMetrics(12.5, 1024, 4096);
    }

    [Fact]
    public async Task A_big_backlog_in_one_file_does_not_keep_another_file_waiting()
    {
        var big = Path.Combine(_dir, "big.log");
        var small = Path.Combine(_dir, "small.log");
        var line = new string('x', 999) + "\n";
        File.WriteAllText(big, string.Concat(Enumerable.Repeat(line, 3000))); // 3 MB: three polls at most 1 MB each
        File.WriteAllText(small, "the small file's line\n");

        var options = Microsoft.Extensions.Options.Options.Create(new AgentOptions
        {
            LogPollInterval = TimeSpan.FromMilliseconds(50),
            BufferCapacity = 10_000,
            ReadExistingLogs = true,
            LogFiles = [new LogFileOptions { Path = big }, new LogFileOptions { Path = small }, new LogFileOptions { Path = Path.Combine(_dir, "missing", "x.log") }],
        });
        var buffers = new AgentBuffers(options);
        var checkpoints = new CheckpointStore(Path.Combine(_dir, "state"), NullLogger<CheckpointStore>.Instance);
        using var service = new LogTailService(
            buffers, checkpoints, options, new HostingEnvironment { ContentRootPath = _dir }, TimeProvider.System, NullLogger<LogTailService>.Instance);

        await service.StartAsync(CancellationToken.None);
        var received = new List<PendingLog>();
        await EventuallyAsync(() =>
        {
            received.AddRange(Drain(buffers.Logs.Reader));
            return received.Count == 3001;
        });
        await service.StopAsync(CancellationToken.None);

        var smallIndex = received.FindIndex(p => p.Entry.Message == "the small file's line");
        Assert.InRange(smallIndex, 0, 1100); // after about 1 MB of the big file, not after all 3 MB
        Assert.Equal(0, checkpoints.Find(small)?.Offset); // start positions are saved before anything is sent
        Assert.Equal(0, checkpoints.Find(big)?.Offset);
    }

    [Fact]
    public async Task A_failed_metrics_reading_does_not_stop_the_sampler()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new AgentOptions
        {
            MetricsInterval = TimeSpan.FromMilliseconds(50),
            IdleMetricsInterval = TimeSpan.FromMilliseconds(50),
        });
        var buffers = new AgentBuffers(options);
        var collector = new FlakyCollector();
        using var sampler = new MetricsSampler(
            collector, buffers, new ViewerActivity(NullLogger<ViewerActivity>.Instance), options, TimeProvider.System, NullLogger<MetricsSampler>.Instance);

        await sampler.StartAsync(CancellationToken.None);
        await EventuallyAsync(() => buffers.Metrics.Reader.Count > 0);
        await sampler.StopAsync(CancellationToken.None);

        Assert.True(collector.Calls >= 2);
        Assert.True(buffers.Metrics.Reader.TryRead(out var sample));
        Assert.Equal((12.5, 1024L, 4096L), (sample.CpuPercent, sample.MemoryUsedMb, sample.MemoryTotalMb));
    }
}
