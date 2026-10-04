using Dapper;
using LogPulse.Core.Models;

namespace LogPulse.Data.Daos;

public sealed class SqlMetricDao(IDbConnectionFactory connectionFactory) : IMetricDao
{
    private const string Columns = "Id, ServerId, Timestamp, CpuPercent, MemoryUsedMb, MemoryTotalMb, DiskUsedPercent";

    private static readonly string[] InsertColumns =
        ["ServerId", "Timestamp", "CpuPercent", "MemoryUsedMb", "MemoryTotalMb", "DiskUsedPercent"];

    public async Task<int> InsertBatchAsync(IReadOnlyCollection<MetricSample> samples, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0)
        {
            return 0;
        }

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var inserted = await SqlBatch.InsertAsync(
            connection,
            transaction,
            "dbo.MetricSamples",
            InsertColumns,
            samples,
            s => [s.ServerId, s.Timestamp, s.CpuPercent, s.MemoryUsedMb, s.MemoryTotalMb, s.DiskUsedPercent],
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return inserted;
    }

    public async Task<IReadOnlyList<MetricSample>> GetRangeAsync(
        int serverId,
        DateTimeOffset from,
        DateTimeOffset to,
        int maxRows = IMetricDao.DefaultMaxRangeRows,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRows, 1);

        // Newest maxRows inside the range, returned oldest first so charts can plot them directly.
        var sql = $"""
            SELECT {Columns}
              FROM (SELECT TOP (@MaxRows) {Columns}
                      FROM dbo.MetricSamples
                     WHERE ServerId = @ServerId AND Timestamp BETWEEN @From AND @To
                     ORDER BY Timestamp DESC, Id DESC) newest
             ORDER BY Timestamp, Id
            """;

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        var samples = await connection.QueryAsync<MetricSample>(new CommandDefinition(
            sql, new { ServerId = serverId, From = from, To = to, MaxRows = maxRows }, cancellationToken: cancellationToken));
        return samples.AsList();
    }

    public async Task<IReadOnlyList<MetricSample>> GetLatestPerServerAsync(CancellationToken cancellationToken = default)
    {
        // One index seek per server instead of ranking the whole history table.
        var sql = $"""
            SELECT latest.*
              FROM dbo.Servers s
             CROSS APPLY (SELECT TOP (1) {Columns}
                            FROM dbo.MetricSamples m
                           WHERE m.ServerId = s.Id
                           ORDER BY m.Timestamp DESC, m.Id DESC) latest
             ORDER BY latest.ServerId
            """;

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        var samples = await connection.QueryAsync<MetricSample>(new CommandDefinition(sql, cancellationToken: cancellationToken));
        return samples.AsList();
    }

    public Task<int> DeleteOlderThanAsync(
        DateTimeOffset cutoff, int batchSize = DataDefaults.DeleteBatchSize, CancellationToken cancellationToken = default) =>
        SqlBatch.DeleteInBatchesAsync(
            connectionFactory, "dbo.MetricSamples", "Timestamp < @Cutoff", new { Cutoff = cutoff }, batchSize, cancellationToken);
}
