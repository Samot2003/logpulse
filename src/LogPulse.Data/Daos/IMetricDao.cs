using LogPulse.Core.Models;

namespace LogPulse.Data.Daos;

public interface IMetricDao
{
    public const int DefaultMaxRangeRows = 20_000;

    /// <summary>Inserts a batch of samples in a single transaction, using multi-row inserts. Returns the number inserted.</summary>
    Task<int> InsertBatchAsync(IReadOnlyCollection<MetricSample> samples, CancellationToken cancellationToken = default);

    /// <summary>
    /// Samples for one server in [from, to], oldest first (ready for charting). If the range holds more than
    /// <paramref name="maxRows"/> samples, only the most recent ones are returned.
    /// </summary>
    Task<IReadOnlyList<MetricSample>> GetRangeAsync(
        int serverId,
        DateTimeOffset from,
        DateTimeOffset to,
        int maxRows = DefaultMaxRangeRows,
        CancellationToken cancellationToken = default);

    /// <summary>The most recent sample of every server that has reported at least once.</summary>
    Task<IReadOnlyList<MetricSample>> GetLatestPerServerAsync(CancellationToken cancellationToken = default);

    /// <summary>Deletes samples older than the cutoff (history retention), in batches. Returns the number deleted.</summary>
    Task<int> DeleteOlderThanAsync(
        DateTimeOffset cutoff, int batchSize = DataDefaults.DeleteBatchSize, CancellationToken cancellationToken = default);
}
