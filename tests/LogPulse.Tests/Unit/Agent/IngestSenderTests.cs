using LogPulse.Agent.Logs;
using LogPulse.Agent.Options;
using LogPulse.Agent.Shipping;
using LogPulse.Core.Contracts;
using LogPulse.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace LogPulse.Tests.Unit.Agent;

public sealed class IngestSenderTests : IDisposable
{
    private sealed class ScriptedIngestClient : IIngestClient
    {
        public Queue<SendResult> Results { get; } = new();

        /// <summary>Answers log batches by size instead of from <see cref="Results"/> (e.g. a proxy's size limit).</summary>
        public Func<int, SendResult>? LogAnswer { get; set; }

        public List<List<string>> LogBatches { get; } = [];

        public List<int> MetricBatches { get; } = [];

        /// <summary>"logs" or "metrics", in the order they were sent.</summary>
        public List<string> Calls { get; } = [];

        public Task<SendResult> SendLogsAsync(IngestLogBatch batch, CancellationToken cancellationToken)
        {
            LogBatches.Add(batch.Entries.ConvertAll(e => e.Message));
            Calls.Add("logs");
            return Task.FromResult(LogAnswer?.Invoke(batch.Entries.Count) ?? Next());
        }

        public Task<SendResult> SendMetricsAsync(IngestMetricBatch batch, CancellationToken cancellationToken)
        {
            MetricBatches.Add(batch.Samples.Count);
            Calls.Add("metrics");
            return Task.FromResult(Next());
        }

        private SendResult Next() => Results.Count > 0 ? Results.Dequeue() : SendResult.Accepted(viewersOnline: true);
    }

    private const string LogFile = "/var/log/app.log";

    private readonly string _state = Directory.CreateTempSubdirectory("logpulse-sender-").FullName;
    private readonly ScriptedIngestClient _client = new();
    private readonly AgentBuffers _buffers;
    private readonly CheckpointStore _checkpoints;
    private readonly ViewerActivity _viewers = new(NullLogger<ViewerActivity>.Instance);
    private readonly IngestSender _sender;

    public IngestSenderTests()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new AgentOptions { MaxBatchItems = 2, BufferCapacity = 100 });
        _buffers = new AgentBuffers(options);
        _checkpoints = new CheckpointStore(_state, NullLogger<CheckpointStore>.Instance);
        _sender = new IngestSender(_buffers, _client, _checkpoints, _viewers, options, new FakeTimeProvider(), NullLogger<IngestSender>.Instance);
    }

    public void Dispose() => Directory.Delete(_state, recursive: true);

    private void BufferLines(params string[] messages)
    {
        foreach (var message in messages)
        {
            var offset = long.Parse(message.Split(' ')[^1], System.Globalization.CultureInfo.InvariantCulture);
            _buffers.Logs.Writer.TryWrite(new PendingLog(
                new IngestLogEntry { Severity = LogSeverity.Information, Source = "app", Message = message },
                new LogPosition(LogFile, FileFingerprint.Empty, offset)));
        }
    }

    [Fact]
    public async Task Logs_go_in_batches_and_each_confirmed_batch_saves_its_position()
    {
        BufferLines("line 10", "line 20", "line 30");

        Assert.True(await _sender.SendPendingAsync(CancellationToken.None));

        Assert.Equal([["line 10", "line 20"], ["line 30"]], _client.LogBatches);
        Assert.Equal(30, _checkpoints.Find(LogFile)?.Offset);
    }

    [Fact]
    public async Task A_batch_that_failed_temporarily_is_sent_again_first_and_its_position_waits()
    {
        BufferLines("line 10", "line 20");
        _client.Results.Enqueue(SendResult.RetryLater("HTTP 503"));

        Assert.False(await _sender.SendPendingAsync(CancellationToken.None));
        Assert.Null(_checkpoints.Find(LogFile));
        Assert.Equal(1, _sender.ConsecutiveFailures);

        BufferLines("line 30");
        Assert.True(await _sender.SendPendingAsync(CancellationToken.None));

        Assert.Equal([["line 10", "line 20"], ["line 10", "line 20"], ["line 30"]], _client.LogBatches);
        Assert.Equal(30, _checkpoints.Find(LogFile)?.Offset);
        Assert.Equal(0, _sender.ConsecutiveFailures);
    }

    [Fact]
    public async Task A_batch_rejected_for_good_is_dropped_so_it_cannot_block_the_rest()
    {
        BufferLines("line 10", "line 20", "line 30");
        _client.Results.Enqueue(SendResult.Rejected("HTTP 400"));

        Assert.True(await _sender.SendPendingAsync(CancellationToken.None));

        Assert.Equal(2, _client.LogBatches.Count);
        Assert.Equal(30, _checkpoints.Find(LogFile)?.Offset);
    }

    [Fact]
    public async Task Metrics_go_first_so_a_stuck_log_batch_does_not_hide_them()
    {
        BufferLines("line 10");
        _buffers.Metrics.Writer.TryWrite(new IngestMetricSample());
        _client.LogAnswer = _ => SendResult.RetryLater("HTTP 500"); // e.g. an API bug with this content

        Assert.False(await _sender.SendPendingAsync(CancellationToken.None));
        _buffers.Metrics.Writer.TryWrite(new IngestMetricSample());
        Assert.False(await _sender.SendPendingAsync(CancellationToken.None));

        Assert.Equal(["metrics", "logs", "metrics", "logs"], _client.Calls);
        // Accepted metrics do not end the log outage: the backoff keeps growing and "recovered" is not logged.
        Assert.Equal(2, _sender.ConsecutiveFailures);
    }

    [Fact]
    public async Task Metrics_are_sent_between_log_batches()
    {
        BufferLines("line 10", "line 20", "line 30");
        for (var i = 0; i < 3; i++)
        {
            _buffers.Metrics.Writer.TryWrite(new IngestMetricSample());
        }

        Assert.True(await _sender.SendPendingAsync(CancellationToken.None));

        Assert.Equal(["metrics", "metrics", "logs", "logs"], _client.Calls);
        Assert.Equal([2, 1], _client.MetricBatches);
    }

    [Fact]
    public async Task A_batch_too_large_for_a_proxy_is_split_until_it_fits_and_nothing_is_lost()
    {
        BufferLines("line 10", "line 20");
        _client.LogAnswer = count => count > 1 ? SendResult.TooLarge("HTTP 413") : SendResult.Accepted(viewersOnline: true);

        Assert.True(await _sender.SendPendingAsync(CancellationToken.None));

        Assert.Equal([["line 10", "line 20"], ["line 10"], ["line 20"]], _client.LogBatches);
        Assert.Equal(20, _checkpoints.Find(LogFile)?.Offset);
    }

    [Fact]
    public async Task A_single_entry_still_too_large_is_dropped()
    {
        BufferLines("line 10", "line 20");
        _client.LogAnswer = _ => SendResult.TooLarge("HTTP 413");

        Assert.True(await _sender.SendPendingAsync(CancellationToken.None));

        Assert.Equal(3, _client.LogBatches.Count);
        Assert.Equal(20, _checkpoints.Find(LogFile)?.Offset);
    }

    [Fact]
    public async Task The_viewers_flag_of_each_response_reaches_the_sampler()
    {
        _buffers.Metrics.Writer.TryWrite(new IngestMetricSample());
        _client.Results.Enqueue(SendResult.Accepted(viewersOnline: false));

        await _sender.SendPendingAsync(CancellationToken.None);

        Assert.False(_viewers.ViewersOnline);
    }
}
