using Dapper;
using LogPulse.Core.Models;

namespace LogPulse.Data.Daos;

public sealed class SqlMetricDao(IDbConnectionFactory connectionFactory) : IMetricDao
{
    private const string Columns = "Id, ServerId, Timestamp, CpuPercent, MemoryUsedMb, MemoryTotalMb, DiskUsedPercent";

    public async Task<int> InsertBatchAsync(IReadOnlyCollection<MetricSample> samples, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0)
        {
            return 0;
        }

        const string sql = """
            INSERT INTO dbo.MetricSamples (ServerId, Timestamp, CpuPercent, MemoryUsedMb, MemoryTotalMb, DiskUsedPercent)
            VALUES (@ServerId, @Timestamp, @CpuPercent, @MemoryUsedMb, @MemoryTotalMb, @DiskUsedPercent)
            """;

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var inserted = await connection.ExecuteAsync(
            new CommandDefinition(sql, samples, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return inserted;
    }

    public async Task<IReadOnlyList<MetricSample>> GetRangeAsync(
        int serverId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
    {
        var sql = $"""
            SELECT {Columns}
              FROM dbo.MetricSamples
             WHERE ServerId = @ServerId AND Timestamp BETWEEN @From AND @To
             ORDER BY Timestamp
            """;

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        var samples = await connection.QueryAsync<MetricSample>(new CommandDefinition(
            sql, new { ServerId = serverId, From = from, To = to }, cancellationToken: cancellationToken));
        return samples.AsList();
    }

    public async Task<IReadOnlyList<MetricSample>> GetLatestPerServerAsync(CancellationToken cancellationToken = default)
    {
        var sql = $"""
            SELECT {Columns}
              FROM (SELECT *, ROW_NUMBER() OVER (PARTITION BY ServerId ORDER BY Timestamp DESC, Id DESC) AS rn
                      FROM dbo.MetricSamples) latest
             WHERE rn = 1
             ORDER BY ServerId
            """;

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        var samples = await connection.QueryAsync<MetricSample>(new CommandDefinition(sql, cancellationToken: cancellationToken));
        return samples.AsList();
    }

    public async Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        return await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM dbo.MetricSamples WHERE Timestamp < @Cutoff", new { Cutoff = cutoff }, cancellationToken: cancellationToken));
    }
}
