using Dapper;
using LogPulse.Core.Models;
using LogPulse.Core.Queries;

namespace LogPulse.Data.Daos;

public sealed class SqlLogDao(IDbConnectionFactory connectionFactory) : ILogDao
{
    public async Task<int> InsertBatchAsync(IReadOnlyCollection<LogEntry> entries, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
        {
            return 0;
        }

        const string sql = """
            INSERT INTO dbo.LogEntries (ServerId, Timestamp, Severity, Source, Message, Exception)
            VALUES (@ServerId, @Timestamp, @Severity, @Source, @Message, @Exception)
            """;

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var inserted = await connection.ExecuteAsync(
            new CommandDefinition(sql, entries, transaction, cancellationToken: cancellationToken));
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

    public async Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        return await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM dbo.LogEntries WHERE Timestamp < @Cutoff", new { Cutoff = cutoff }, cancellationToken: cancellationToken));
    }
}
