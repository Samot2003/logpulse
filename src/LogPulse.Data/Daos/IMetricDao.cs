using LogPulse.Core.Models;

namespace LogPulse.Data.Daos;

public interface IMetricDao
{
    Task<int> InsertBatchAsync(IReadOnlyCollection<MetricSample> samples, CancellationToken cancellationToken = default);

    /// <summary>Samples for one server in [from, to], oldest first (ready for charting).</summary>
    Task<IReadOnlyList<MetricSample>> GetRangeAsync(
        int serverId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default);

    /// <summary>The most recent sample of every server that has reported at least once.</summary>
    Task<IReadOnlyList<MetricSample>> GetLatestPerServerAsync(CancellationToken cancellationToken = default);

    Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default);
}
