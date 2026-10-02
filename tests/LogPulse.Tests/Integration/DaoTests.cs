using LogPulse.Core.Models;
using LogPulse.Core.Queries;
using LogPulse.Data;
using LogPulse.Data.Daos;

namespace LogPulse.Tests.Integration;

[Collection(SqlServerGroup.Name)]
[Trait("Category", "Integration")]
public class DaoTests(SqlServerFixture fixture)
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly SqlServerDao _servers = new(fixture.ConnectionFactory);
    private readonly SqlLogDao _logs = new(fixture.ConnectionFactory);
    private readonly SqlMetricDao _metrics = new(fixture.ConnectionFactory);

    // Each test uses its own server name so tests stay independent on the shared database.
    private Task<Server> NewServerAsync() => _servers.UpsertAsync($"srv-{Guid.NewGuid():N}", T0);

    [Fact]
    public async Task Schema_initializer_is_idempotent()
    {
        await new DatabaseInitializer(fixture.ConnectionFactory).InitializeAsync();
    }

    [Fact]
    public async Task Upsert_registers_once_and_updates_last_seen()
    {
        var name = $"srv-{Guid.NewGuid():N}";

        var first = await _servers.UpsertAsync(name, T0);
        var second = await _servers.UpsertAsync(name, T0.AddMinutes(5));

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(T0, second.RegisteredAt);
        Assert.Equal(T0.AddMinutes(5), second.LastSeenAt);
        Assert.Equal(second, await _servers.GetByIdAsync(first.Id));
    }

    [Fact]
    public async Task Concurrent_upserts_of_same_name_create_a_single_server()
    {
        var name = $"srv-{Guid.NewGuid():N}";

        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => _servers.UpsertAsync(name, T0)));

        Assert.Single(results.Select(s => s.Id).Distinct());
    }

    [Fact]
    public async Task Log_query_filters_by_severity_search_and_time_and_pages_newest_first()
    {
        var server = await NewServerAsync();
        var entries = Enumerable.Range(0, 30).Select(i => new LogEntry
        {
            ServerId = server.Id,
            Timestamp = T0.AddMinutes(i),
            Severity = i % 3 == 0 ? LogSeverity.Error : LogSeverity.Information,
            Source = "IIS",
            Message = i % 2 == 0 ? $"Request {i} timed out (100%)" : $"Request {i} ok",
        }).ToList();

        Assert.Equal(30, await _logs.InsertBatchAsync(entries));

        var errors = await _logs.QueryAsync(new LogQuery { ServerId = server.Id, MinSeverity = LogSeverity.Error, PageSize = 4 });
        Assert.Equal(10, errors.TotalCount);
        Assert.Equal(3, errors.TotalPages);
        Assert.Equal([27, 24, 21, 18], errors.Items.Select(e => (int)(e.Timestamp - T0).TotalMinutes));

        // '%' in the search must match literally, not as a wildcard.
        var literal = await _logs.QueryAsync(new LogQuery { ServerId = server.Id, Search = "(100%)" });
        Assert.Equal(15, literal.TotalCount);

        var window = await _logs.QueryAsync(new LogQuery { ServerId = server.Id, From = T0.AddMinutes(10), To = T0.AddMinutes(14) });
        Assert.Equal(5, window.TotalCount);
    }

    [Fact]
    public async Task Log_retention_deletes_only_old_entries()
    {
        var server = await NewServerAsync();
        await _logs.InsertBatchAsync(
        [
            new LogEntry { ServerId = server.Id, Timestamp = T0.AddYears(-10), Source = "app", Message = "ancient" },
            new LogEntry { ServerId = server.Id, Timestamp = T0, Source = "app", Message = "recent" },
        ]);

        await _logs.DeleteOlderThanAsync(T0.AddYears(-5));

        var remaining = await _logs.QueryAsync(new LogQuery { ServerId = server.Id });
        Assert.Equal("recent", Assert.Single(remaining.Items).Message);
    }

    [Fact]
    public async Task Metrics_range_is_oldest_first_and_latest_is_per_server()
    {
        var a = await NewServerAsync();
        var b = await NewServerAsync();
        await _metrics.InsertBatchAsync(
        [
            Sample(a.Id, 0, cpu: 10), Sample(a.Id, 1, cpu: 20), Sample(a.Id, 2, cpu: 30),
            Sample(b.Id, 0, cpu: 50),
        ]);

        var range = await _metrics.GetRangeAsync(a.Id, T0, T0.AddMinutes(1));
        Assert.Equal([10d, 20d], range.Select(s => s.CpuPercent));

        var latest = await _metrics.GetLatestPerServerAsync();
        Assert.Equal(30, latest.Single(s => s.ServerId == a.Id).CpuPercent);
        Assert.Equal(50, latest.Single(s => s.ServerId == b.Id).CpuPercent);
    }

    private static MetricSample Sample(int serverId, int minute, double cpu) => new()
    {
        ServerId = serverId,
        Timestamp = T0.AddMinutes(minute),
        CpuPercent = cpu,
        MemoryUsedMb = 4096,
        MemoryTotalMb = 16384,
        DiskUsedPercent = 42.5,
    };
}
