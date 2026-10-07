using LogPulse.Api.Live;
using LogPulse.Core.Contracts;
using LogPulse.Core.Models;
using LogPulse.Data.Daos;

namespace LogPulse.Api.Ingest;

/// <summary>
/// Stores batches sent by an agent. The server always comes from the agent's token, never from the payload,
/// and its last-seen time uses the API's clock so a skewed agent clock cannot make it look online or offline.
/// Once a batch is stored, dashboard viewers are told about it.
/// </summary>
public sealed class IngestService(IServerDao servers, ILogDao logs, IMetricDao metrics, ILiveUpdates live, TimeProvider time)
{
    public async Task<int> IngestLogsAsync(string serverName, IngestLogBatch batch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);

        var now = time.GetUtcNow();
        var server = await servers.UpsertAsync(serverName, now, cancellationToken);
        var entries = batch.Entries
            .Select(e => new LogEntry
            {
                ServerId = server.Id,
                Timestamp = ClampFuture(e.Timestamp, now),
                Severity = e.Severity,
                // Normalize instead of rejecting: one odd line must not make the agent lose the whole batch.
                Source = string.IsNullOrWhiteSpace(e.Source) ? UnknownSource : FieldLimits.Truncate(e.Source, FieldLimits.LogSource),
                Message = FieldLimits.Truncate(e.Message, FieldLimits.LogMessage),
                Exception = e.Exception is null ? null : FieldLimits.Truncate(e.Exception, FieldLimits.LogException),
            })
            .ToList();

        var stored = await logs.InsertBatchAsync(entries, cancellationToken);
        await live.LogsStoredAsync(server, entries);
        return stored;
    }

    public async Task<int> IngestMetricsAsync(string serverName, IngestMetricBatch batch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);

        var now = time.GetUtcNow();
        var server = await servers.UpsertAsync(serverName, now, cancellationToken);
        var samples = batch.Samples
            .Select(s => new MetricSample
            {
                ServerId = server.Id,
                Timestamp = ClampFuture(s.Timestamp, now),
                CpuPercent = s.CpuPercent,
                MemoryUsedMb = s.MemoryUsedMb,
                MemoryTotalMb = s.MemoryTotalMb,
                DiskUsedPercent = s.DiskUsedPercent,
            })
            .ToList();

        var stored = await metrics.InsertBatchAsync(samples, cancellationToken);
        await live.MetricsStoredAsync(server, samples);
        return stored;
    }

    public const string UnknownSource = "unknown";

    /// <summary>
    /// A timestamp from the future would stay on top of the newest-first viewer and never be purged by retention,
    /// so anything beyond the allowed clock skew is replaced by the receive time.
    /// </summary>
    public static DateTimeOffset ClampFuture(DateTimeOffset timestamp, DateTimeOffset now) =>
        timestamp > now + IngestLimits.MaxClockSkew ? now : timestamp;
}
