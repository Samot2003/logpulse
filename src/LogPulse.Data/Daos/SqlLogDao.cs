using Dapper;
using LogPulse.Core.Models;
using LogPulse.Core.Queries;

namespace LogPulse.Data.Daos;

public sealed class SqlLogDao(IDbConnectionFactory connectionFactory) : ILogDao
{
    private static readonly string[] InsertColumns = ["ServerId", "Timestamp", "Severity", "Source", "Message", "Exception"];

    public async Task<int> InsertBatchAsync(IReadOnlyCollection<LogEntry> entries, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
        {
            return 0;
        }

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var inserted = await SqlBatch.InsertAsync(
            connection,
            transaction,
            "dbo.LogEntries",
            InsertColumns,
            entries,
            e => [e.ServerId, e.Timestamp, (byte)e.Severity, e.Source, e.Message, e.Exception],
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return inserted;
    }

    public async Task<PagedResult<LogEntry>> QueryAsync(LogQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        query = query.Normalized();

        var (where, parameters) = LogQuerySql.BuildWhere(query);
        parameters.Add("Offset", query.Offset);
        parameters.Add("PageSize", query.PageSize);

        var sql = $"""
            SELECT COUNT_BIG(*) FROM dbo.LogEntries {where};

            SELECT Id, ServerId, Timestamp, Severity, Source, Message, Exception
              FROM dbo.LogEntries
              {where}
             ORDER BY Timestamp DESC, Id DESC
            OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY;
            """;

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var grid = await connection.QueryMultipleAsync(
            new CommandDefinition(sql, parameters, cancellationToken: cancellationToken));
        var total = await grid.ReadSingleAsync<long>();
        var items = (await grid.ReadAsync<LogEntry>()).AsList();
        return new PagedResult<LogEntry>(items, query.Page, query.PageSize, total);
    }

    public Task<int> DeleteOlderThanAsync(
        DateTimeOffset cutoff, int batchSize = DataDefaults.DeleteBatchSize, CancellationToken cancellationToken = default) =>
        SqlBatch.DeleteInBatchesAsync(
            connectionFactory, "dbo.LogEntries", "Timestamp < @Cutoff", new { Cutoff = cutoff }, batchSize, cancellationToken);
}
