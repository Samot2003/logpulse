using LogPulse.Api.Live;
using LogPulse.Core.Contracts;
using LogPulse.Core.Models;
using LogPulse.Core.Queries;
using LogPulse.Dashboard.Api;
using LogPulse.Dashboard.Live;

namespace LogPulse.Tests.Fakes;

/// <summary>Records what the API would broadcast to viewers.</summary>
internal sealed class RecordingLiveUpdates : ILiveUpdates
{
    public List<(Server Server, IReadOnlyList<LogEntry> Entries)> Logs { get; } = [];

    public List<(Server Server, IReadOnlyList<MetricSample> Samples)> Metrics { get; } = [];

    public Task LogsStoredAsync(Server server, IReadOnlyList<LogEntry> entries)
    {
        Logs.Add((server, entries));
        return Task.CompletedTask;
    }

    public Task MetricsStoredAsync(Server server, IReadOnlyList<MetricSample> samples)
    {
        Metrics.Add((server, samples));
        return Task.CompletedTask;
    }
}

/// <summary>A live feed the test drives by hand.</summary>
internal sealed class FakeLiveFeed : ILiveFeed
{
    public event Action<LogsReceivedEvent>? LogsReceived;

    public event Action<MetricsReceivedEvent>? MetricsReceived;

    public event Action<LiveStatus>? StatusChanged;

    public LiveStatus Status { get; private set; } = LiveStatus.Live;

    public int Starts { get; private set; }

    public bool HasSubscribers => LogsReceived is not null || MetricsReceived is not null || StatusChanged is not null;

    public void Start() => Starts++;

    public void Push(LogsReceivedEvent received) => LogsReceived?.Invoke(received);

    public void Push(MetricsReceivedEvent received) => MetricsReceived?.Invoke(received);

    public void SetStatus(LiveStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(status);
    }
}

/// <summary>An API with data set by the test; records the log queries it receives.</summary>
internal sealed class FakeLogPulseApi : ILogPulseApi
{
    public List<Server> Servers { get; } = [];

    public List<MetricSample> Metrics { get; } = [];

    public List<LogEntry> Logs { get; } = [];

    public List<LogQuery> LogQueries { get; } = [];

    public List<(int ServerId, DateTimeOffset From, DateTimeOffset To)> MetricQueries { get; } = [];

    /// <summary>When set, every call throws it.</summary>
    public Exception? Failure { get; set; }

    public int ServerCalls { get; private set; }

    public Task<IReadOnlyList<Server>> GetServersAsync(CancellationToken cancellationToken = default)
    {
        ServerCalls++;
        return Answer<IReadOnlyList<Server>>([.. Servers]);
    }

    public Task<IReadOnlyList<MetricSample>> GetLatestMetricsAsync(CancellationToken cancellationToken = default) =>
        Answer<IReadOnlyList<MetricSample>>([.. Metrics.GroupBy(m => m.ServerId).Select(g => g.MaxBy(m => m.Timestamp)!)]);

    public Task<IReadOnlyList<MetricSample>> GetMetricsAsync(int serverId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
    {
        MetricQueries.Add((serverId, from, to));
        return Answer<IReadOnlyList<MetricSample>>([.. Metrics.Where(m => m.ServerId == serverId && m.Timestamp >= from && m.Timestamp <= to).OrderBy(m => m.Timestamp)]);
    }

    public Task<PagedResult<LogEntry>> GetLogsAsync(LogQuery query, CancellationToken cancellationToken = default)
    {
        LogQueries.Add(query);
        var matching = Logs
            .Where(l => query.ServerId is null || l.ServerId == query.ServerId)
            .Where(l => query.MinSeverity is null || l.Severity >= query.MinSeverity)
            .OrderByDescending(l => l.Timestamp)
            .ToList();
        return Answer(new PagedResult<LogEntry>([.. matching.Skip((int)query.Offset).Take(query.PageSize)], query.Page, query.PageSize, matching.Count));
    }

    private Task<T> Answer<T>(T value) => Failure is { } failure ? Task.FromException<T>(failure) : Task.FromResult(value);
}
