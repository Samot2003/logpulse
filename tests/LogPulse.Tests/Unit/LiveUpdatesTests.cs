using LogPulse.Api.Ingest;
using LogPulse.Api.Live;
using LogPulse.Core.Contracts;
using LogPulse.Core.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;

namespace LogPulse.Tests.Unit;

public class LiveUpdatesTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly Server Web01 = new(7, "web-01", Now.AddDays(-1), Now);

    private readonly ViewerTracker _viewers = new();
    private readonly RecordingClients _clients = new();

    private HubLiveUpdates Live() => new(new FakeHubContext(_clients), _viewers, NullLogger<HubLiveUpdates>.Instance);

    private static LogEntry Entry(LogSeverity severity) => new() { ServerId = Web01.Id, Severity = severity, Source = "app", Message = "m" };

    [Fact]
    public void The_tracker_counts_open_connections()
    {
        Assert.False(_viewers.AnyViewers);

        _viewers.Connected();
        _viewers.Connected();
        _viewers.Disconnected();

        Assert.Equal(1, _viewers.Connections);
        Assert.True(_viewers.AnyViewers);
        _viewers.Disconnected();
        Assert.False(_viewers.AnyViewers);
    }

    [Fact]
    public async Task Stored_logs_are_announced_with_their_count_and_highest_severity()
    {
        _viewers.Connected();

        await Live().LogsStoredAsync(Web01, [Entry(LogSeverity.Information), Entry(LogSeverity.Error), Entry(LogSeverity.Warning)]);

        Assert.Equal(new LogsReceivedEvent(7, "web-01", 3, LogSeverity.Error, Now), Assert.Single(_clients.Recorder.Logs));
    }

    [Fact]
    public async Task Stored_metrics_travel_whole()
    {
        _viewers.Connected();
        MetricSample[] samples = [new() { ServerId = 7, Timestamp = Now, CpuPercent = 42 }];

        await Live().MetricsStoredAsync(Web01, samples);

        var received = Assert.Single(_clients.Recorder.Metrics);
        Assert.Equal((7, "web-01", Now), (received.ServerId, received.ServerName, received.LastSeenAt));
        Assert.Equal(samples, received.Samples);
    }

    [Fact]
    public async Task Nothing_is_sent_without_viewers_or_without_data()
    {
        await Live().LogsStoredAsync(Web01, [Entry(LogSeverity.Error)]);
        _viewers.Connected();
        await Live().LogsStoredAsync(Web01, []);
        await Live().MetricsStoredAsync(Web01, []);

        Assert.Empty(_clients.Recorder.Logs);
        Assert.Empty(_clients.Recorder.Metrics);
    }

    [Fact]
    public async Task A_failed_broadcast_does_not_fail_the_ingestion()
    {
        _viewers.Connected();
        _clients.Recorder.Failure = new IOException("connection reset");

        await Live().LogsStoredAsync(Web01, [Entry(LogSeverity.Error)]);
    }

    [Fact]
    public async Task A_stuck_viewer_connection_holds_the_request_for_the_send_timeout_at_most()
    {
        _viewers.Connected();
        _clients.Recorder.Hang = new TaskCompletionSource();

        var send = Live().LogsStoredAsync(Web01, [Entry(LogSeverity.Error)]);

        var finished = await Task.WhenAny(send, Task.Delay(HubLiveUpdates.SendTimeout * 5));
        Assert.Same(send, finished);
        await send;
    }

    private sealed class RecordingClient : ILiveClient
    {
        public List<LogsReceivedEvent> Logs { get; } = [];

        public List<MetricsReceivedEvent> Metrics { get; } = [];

        public Exception? Failure { get; set; }

        public TaskCompletionSource? Hang { get; set; }

        public Task LogsReceived(LogsReceivedEvent received)
        {
            Logs.Add(received);
            return Answer();
        }

        public Task MetricsReceived(MetricsReceivedEvent received)
        {
            Metrics.Add(received);
            return Answer();
        }

        private Task Answer() => Hang?.Task ?? (Failure is null ? Task.CompletedTask : Task.FromException(Failure));
    }

    private sealed class RecordingClients : IHubClients<ILiveClient>
    {
        public RecordingClient Recorder { get; } = new();

        public ILiveClient All => Recorder;

        public ILiveClient AllExcept(IReadOnlyList<string> excludedConnectionIds) => throw new NotSupportedException();

        public ILiveClient Client(string connectionId) => throw new NotSupportedException();

        public ILiveClient Clients(IReadOnlyList<string> connectionIds) => throw new NotSupportedException();

        public ILiveClient Group(string groupName) => throw new NotSupportedException();

        public ILiveClient GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => throw new NotSupportedException();

        public ILiveClient Groups(IReadOnlyList<string> groupNames) => throw new NotSupportedException();

        public ILiveClient User(string userId) => throw new NotSupportedException();

        public ILiveClient Users(IReadOnlyList<string> userIds) => throw new NotSupportedException();
    }

    private sealed class FakeHubContext(IHubClients<ILiveClient> clients) : IHubContext<LiveHub, ILiveClient>
    {
        public IHubClients<ILiveClient> Clients => clients;

        public IGroupManager Groups => throw new NotSupportedException();
    }
}
