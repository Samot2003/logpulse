using System.Data.Common;
using System.Text;
using Dapper;

namespace LogPulse.Data;

/// <summary>Helpers for set-based writes: multi-row inserts and batched deletes.</summary>
internal static class SqlBatch
{
    // SQL Server accepts at most 2100 parameters per command and 1000 rows per VALUES list.
    private const int MaxParametersPerCommand = 2000;
    private const int MaxRowsPerValuesList = 1000;

    /// <summary>
    /// Inserts the rows with as few round trips as possible: one multi-row INSERT per chunk,
    /// all inside the caller's transaction. Returns the number of rows inserted.
    /// </summary>
    public static async Task<int> InsertAsync<T>(
        DbConnection connection,
        DbTransaction transaction,
        string table,
        IReadOnlyList<string> columns,
        IReadOnlyCollection<T> rows,
        Func<T, object?[]> values,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfZero(columns.Count);

        var rowsPerCommand = Math.Clamp(MaxParametersPerCommand / columns.Count, 1, MaxRowsPerValuesList);
        var inserted = 0;

        foreach (var chunk in rows.Chunk(rowsPerCommand))
        {
            var sql = new StringBuilder($"INSERT INTO {table} ({string.Join(", ", columns)}) VALUES ");
            var parameters = new DynamicParameters();

            for (var row = 0; row < chunk.Length; row++)
            {
                var rowValues = values(chunk[row]);
                if (rowValues.Length != columns.Count)
                {
                    throw new InvalidOperationException(
                        $"Expected {columns.Count} values per row for {table} but got {rowValues.Length}.");
                }

                sql.Append(row == 0 ? "(" : ", (");
                for (var col = 0; col < columns.Count; col++)
                {
                    var name = $"p{row}_{col}";
                    sql.Append(col == 0 ? "@" : ", @").Append(name);
                    parameters.Add(name, rowValues[col]);
                }

                sql.Append(')');
            }

            inserted += await connection.ExecuteAsync(
                new CommandDefinition(sql.ToString(), parameters, transaction, cancellationToken: cancellationToken));
        }

        return inserted;
    }

    /// <summary>
    /// Runs <c>DELETE TOP (@BatchSize) ...</c> until fewer rows than the batch size are deleted, so a large purge
    /// never holds one huge transaction. Returns the total number deleted.
    /// </summary>
    public static async Task<int> DeleteInBatchesAsync(
        IDbConnectionFactory connectionFactory,
        string table,
        string where,
        object parameters,
        int batchSize,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);

        var sql = $"DELETE TOP (@BatchSize) FROM {table} WHERE {where}";
        var dynamicParameters = new DynamicParameters(parameters);
        dynamicParameters.Add("BatchSize", batchSize);

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        var total = 0;
        int deleted;
        do
        {
            deleted = await connection.ExecuteAsync(
                new CommandDefinition(sql, dynamicParameters, cancellationToken: cancellationToken));
            total += deleted;
        }
        while (deleted == batchSize);

        return total;
    }
}
