using LogPulse.Core.Models;
using LogPulse.Core.Queries;

namespace LogPulse.Data.Daos;

public interface ILogDao
{
    /// <summary>Inserts a batch of entries in a single transaction, using multi-row inserts. Returns the number inserted.</summary>
    Task<int> InsertBatchAsync(IReadOnlyCollection<LogEntry> entries, CancellationToken cancellationToken = default);

    Task<PagedResult<LogEntry>> QueryAsync(LogQuery query, CancellationToken cancellationToken = default);

    /// <summary>Deletes entries older than the cutoff (history retention), in batches. Returns the number deleted.</summary>
    Task<int> DeleteOlderThanAsync(
        DateTimeOffset cutoff, int batchSize = DataDefaults.DeleteBatchSize, CancellationToken cancellationToken = default);
}
