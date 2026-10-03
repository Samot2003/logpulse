using Dapper;
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
    public async Task Schema_initializer_is_idempotent_and_creates_all_indexes()
    {
        // The fixture already ran it once; a second run must not fail or duplicate anything.
        await new DatabaseInitializer(fixture.ConnectionFactory).InitializeAsync();

        await using var connection = await fixture.ConnectionFactory.OpenAsync();
        var indexes = (await connection.QueryAsync<string>(
            """
            SELECT name FROM sys.indexes
             WHERE object_id IN (OBJECT_ID(N'dbo.LogEntries'), OBJECT_ID(N'dbo.MetricSamples'))
               AND name LIKE 'IX[_]%'
             ORDER BY name
            """)).AsList();

        Assert.Equal(
            ["IX_LogEntries_Server_Timestamp", "IX_LogEntries_Timestamp", "IX_MetricSamples_Server_Timestamp", "IX_MetricSamples_Timestamp"],
            indexes);

        // Newest-first indexes must end in Id DESC so ORDER BY Timestamp DESC, Id DESC needs no sort.
        var withIdDesc = (await connection.QueryAsync<string>(
            """
            SELECT i.name FROM sys.indexes i
              JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
              JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
             WHERE i.object_id IN (OBJECT_ID(N'dbo.LogEntries'), OBJECT_ID(N'dbo.MetricSamples'))
               AND c.name = 'Id' AND ic.is_descending_key = 1 AND ic.is_included_column = 0
             ORDER BY i.name
            """)).AsList();

        Assert.Equal(["IX_LogEntries_Server_Timestamp", "IX_LogEntries_Timestamp", "IX_MetricSamples_Server_Timestamp"], withIdDesc);
    }

    [Fact]
    public async Task Schema_initializer_recreates_an_index_missing_after_an_interrupted_startup()
    {
        await using var connection = await fixture.ConnectionFactory.OpenAsync();
        await connection.ExecuteAsync("DROP INDEX IX_MetricSamples_Timestamp ON dbo.MetricSamples");

        await new DatabaseInitializer(fixture.ConnectionFactory).InitializeAsync();

        var exists = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM sys.indexes WHERE name = N'IX_MetricSamples_Timestamp' AND object_id = OBJECT_ID(N'dbo.MetricSamples')");
        Assert.Equal(1, exists);
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
    public async Task Upsert_never_moves_last_seen_backwards()
    {
        var name = $"srv-{Guid.NewGuid():N}";
        await _servers.UpsertAsync(name, T0.AddMinutes(10));

        var afterLateBatch = await _servers.UpsertAsync(name, T0);

        Assert.Equal(T0.AddMinutes(10), afterLateBatch.LastSeenAt);
    }

    [Fact]
    public async Task GetById_returns_null_for_unknown_server()
    {
        Assert.Null(await _servers.GetByIdAsync(int.MaxValue));
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

        var lastPage = await _logs.QueryAsync(new LogQuery { ServerId = server.Id, MinSeverity = LogSeverity.Error, PageSize = 4, Page = 3 });
        Assert.Equal([3, 0], lastPage.Items.Select(e => (int)(e.Timestamp - T0).TotalMinutes));

        var beyondEnd = await _logs.QueryAsync(new LogQuery { ServerId = server.Id, Page = int.MaxValue, PageSize = LogQuery.MaxPageSize });
        Assert.Empty(beyondEnd.Items);
        Assert.Equal(30, beyondEnd.TotalCount);

        // From and To are both inclusive.
        var window = await _logs.QueryAsync(new LogQuery { ServerId = server.Id, From = T0.AddMinutes(10), To = T0.AddMinutes(14) });
        Assert.Equal(5, window.TotalCount);
    }

    [Fact]
    public async Task Log_search_treats_wildcards_literally_and_matches_source()
    {
        var server = await NewServerAsync();
        await _logs.InsertBatchAsync(
        [
            new LogEntry { ServerId = server.Id, Timestamp = T0, Source = "disk", Message = "Usage at 100% on C:" },
            // Would match "100%" if '%' were treated as a wildcard.
            new LogEntry { ServerId = server.Id, Timestamp = T0, Source = "disk", Message = "Usage at 1000 MB" },
            new LogEntry { ServerId = server.Id, Timestamp = T0, Source = "w3_svc", Message = "started" },
            // Would match "w3_svc" if '_' were treated as a wildcard.
            new LogEntry { ServerId = server.Id, Timestamp = T0, Source = "w3xsvc", Message = "started" },
        ]);

        var percent = await _logs.QueryAsync(new LogQuery { ServerId = server.Id, Search = "100%" });
        Assert.Equal("Usage at 100% on C:", Assert.Single(percent.Items).Message);

        var underscore = await _logs.QueryAsync(new LogQuery { ServerId = server.Id, Search = "w3_svc" });
        Assert.Equal("w3_svc", Assert.Single(underscore.Items).Source);
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
