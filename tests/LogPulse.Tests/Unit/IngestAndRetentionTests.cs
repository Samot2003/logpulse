using LogPulse.Api.Background;
using LogPulse.Api.Ingest;
using LogPulse.Api.Options;
using LogPulse.Core.Contracts;
using LogPulse.Core.Models;
using LogPulse.Core.Queries;
using LogPulse.Data.Daos;
using LogPulse.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace LogPulse.Tests.Unit;

public class IngestServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(Now);
    private readonly InMemoryServerDao _servers = new();
    private readonly RecordingLogDao _logs = new();
    private readonly RecordingMetricDao _metrics = new();
    private readonly IngestService _ingest;

    public IngestServiceTests() => _ingest = new IngestService(_servers, _logs, _metrics, _time);

    [Fact]
    public async Task Logs_are_stored_for_the_authenticated_server_and_last_seen_uses_the_api_clock()
    {
        var agentClock = Now.AddMinutes(-3);
        var batch = new IngestLogBatch
        {
            Entries = [new IngestLogEntry { Timestamp = agentClock, Severity = LogSeverity.Error, Source = "IIS", Message = "boom" }],
        };

        var accepted = await _ingest.IngestLogsAsync("web-01", batch);

        Assert.Equal(1, accepted);
        var server = Assert.Single(_servers.Servers);
        Assert.Equal("web-01", server.Name);
        Assert.Equal(Now, server.LastSeenAt);
        var entry = Assert.Single(_logs.Inserted);
        Assert.Equal(server.Id, entry.ServerId);
        Assert.Equal(agentClock, entry.Timestamp);
        Assert.Equal(LogSeverity.Error, entry.Severity);
    }

    [Fact]
    public async Task Timestamps_beyond_the_allowed_clock_skew_are_clamped_to_the_receive_time()
    {
        var withinSkew = Now + IngestLimits.MaxClockSkew;
        var batch = new IngestLogBatch
        {
            Entries =
            [
                new IngestLogEntry { Timestamp = withinSkew, Source = "a", Message = "ok" },
                new IngestLogEntry { Timestamp = Now.AddYears(5), Source = "a", Message = "future" },
                new IngestLogEntry { Timestamp = DateTimeOffset.MaxValue, Source = "a", Message = "max" },
            ],
        };

        await _ingest.IngestLogsAsync("web-01", batch);

        Assert.Equal([withinSkew, Now, Now], _logs.Inserted.Select(e => e.Timestamp));
    }

    [Fact]
    public async Task Blank_lines_are_accepted_and_a_blank_source_becomes_unknown()
    {
        var batch = new IngestLogBatch { Entries = [new IngestLogEntry { Timestamp = Now, Source = "  ", Message = "" }] };

        Assert.Equal(1, await _ingest.IngestLogsAsync("web-01", batch));

        var entry = Assert.Single(_logs.Inserted);
        Assert.Equal(IngestService.UnknownSource, entry.Source);
        Assert.Equal(string.Empty, entry.Message);
    }

    [Fact]
    public async Task Oversized_fields_are_truncated_instead_of_rejecting_the_batch()
    {
        var batch = new IngestLogBatch
        {
            Entries =
            [
                new IngestLogEntry
                {
                    Source = new string('s', FieldLimits.LogSource + 10),
                    Message = new string('m', FieldLimits.LogMessage + 10),
                    Exception = new string('e', FieldLimits.LogException + 10),
                },
            ],
        };

        await _ingest.IngestLogsAsync("web-01", batch);

        var entry = Assert.Single(_logs.Inserted);
        Assert.Equal(FieldLimits.LogSource, entry.Source.Length);
        Assert.Equal(FieldLimits.LogMessage, entry.Message.Length);
        Assert.Equal(FieldLimits.LogException, entry.Exception!.Length);
        Assert.EndsWith("…", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Truncate_never_splits_a_surrogate_pair()
    {
        var value = new string('a', 8) + "😀" + "tail"; // the emoji occupies indexes 8 and 9

        var truncated = IngestService.Truncate(value, 10);

        Assert.Equal(new string('a', 8) + "…", truncated);
        Assert.Equal("short", IngestService.Truncate("short", 10));
    }

    [Fact]
    public async Task Metrics_are_stored_for_the_authenticated_server()
    {
        var batch = new IngestMetricBatch
        {
            Samples =
            [
                new IngestMetricSample { Timestamp = Now, CpuPercent = 42.5, MemoryUsedMb = 1024, MemoryTotalMb = 4096, DiskUsedPercent = 70 },
                new IngestMetricSample { Timestamp = Now.AddYears(1), CpuPercent = 1 },
            ],
        };

        Assert.Equal(2, await _ingest.IngestMetricsAsync("db-01", batch));

        var sample = _metrics.Inserted[0];
        Assert.Equal(_servers.Servers.Single().Id, sample.ServerId);
        Assert.Equal(42.5, sample.CpuPercent);
        Assert.Equal(4096, sample.MemoryTotalMb);
        Assert.Equal(Now, _metrics.Inserted[1].Timestamp); // a future sample is clamped to the receive time
    }
}

public class RetentionServiceTests
{
    [Fact]
    public async Task RunOnce_purges_each_dataset_with_its_own_retention_and_the_configured_batch_size()
    {
        var now = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var logs = new RecordingLogDao();
        var metrics = new RecordingMetricDao();
        var tokens = new InMemoryRefreshTokenDao();
        await tokens.InsertAsync(new RefreshToken { TokenHash = [1], ExpiresAt = now.AddSeconds(-1) });
        await tokens.InsertAsync(new RefreshToken { TokenHash = [2], ExpiresAt = now.AddDays(1) });
        var options = new RetentionOptions { LogRetentionDays = 30, MetricRetentionDays = 7, BatchSize = 250 };
        var service = new RetentionService(
            logs, metrics, tokens, Microsoft.Extensions.Options.Options.Create(options), new FakeTimeProvider(now), NullLogger<RetentionService>.Instance);

        var result = await service.RunOnceAsync();

        Assert.Equal((now.AddDays(-30), 250), Assert.Single(logs.Deletes));
        Assert.Equal((now.AddDays(-7), 250), Assert.Single(metrics.Deletes));
        Assert.Equal(new RetentionResult(3, 5, 1), result);
        Assert.Equal([2], Assert.Single(tokens.Tokens).TokenHash);
    }

    [Fact]
    public async Task A_failed_purge_is_logged_and_retried_on_the_next_cycle()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));
        using var logs = new FlakyLogDao(failures: 1);
        var options = new RetentionOptions { Interval = TimeSpan.FromHours(1) };
        using var service = new RetentionService(
            logs, new RecordingMetricDao(), new InMemoryRefreshTokenDao(), Microsoft.Extensions.Options.Options.Create(options), time, NullLogger<RetentionService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await logs.WaitForCallsAsync(1);   // first purge throws, the service keeps running
        time.Advance(TimeSpan.FromHours(1));
        await logs.WaitForCallsAsync(2);   // next cycle runs and succeeds
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(2, logs.Calls);
    }

    /// <summary>Throws on the first purges, then succeeds; lets the test wait for each call.</summary>
    private sealed class FlakyLogDao(int failures) : ILogDao, IDisposable
    {
        public void Dispose() => _called.Dispose();

        private readonly SemaphoreSlim _called = new(0);
        private int _failuresLeft = failures;

        public int Calls { get; private set; }

        public Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, int batchSize = DataDefaults.DeleteBatchSize, CancellationToken cancellationToken = default)
        {
            Calls++;
            _called.Release();
            return _failuresLeft-- > 0 ? throw new InvalidOperationException("database down") : Task.FromResult(0);
        }

        public async Task WaitForCallsAsync(int calls)
        {
            while (Calls < calls)
            {
                Assert.True(await _called.WaitAsync(TimeSpan.FromSeconds(10)), "The retention service did not run in time.");
            }
        }

        public Task<int> InsertBatchAsync(IReadOnlyCollection<LogEntry> entries, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PagedResult<LogEntry>> QueryAsync(LogQuery query, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
