using LogPulse.Core.Contracts;
using LogPulse.Core.Models;
using LogPulse.Data.Daos;

namespace LogPulse.Api.Ingest;

/// <summary>
/// Stores batches sent by an agent. The server always comes from the agent's token, never from the payload,
/// and its last-seen time uses the API's clock so a skewed agent clock cannot make it look online or offline.
/// </summary>
public sealed class IngestService(IServerDao servers, ILogDao logs, IMetricDao metrics, TimeProvider time)
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
                Source = string.IsNullOrWhiteSpace(e.Source) ? UnknownSource : Truncate(e.Source, FieldLimits.LogSource),
                Message = Truncate(e.Message, FieldLimits.LogMessage),
                Exception = e.Exception is null ? null : Truncate(e.Exception, FieldLimits.LogException),
            })
            .ToList();

        return await logs.InsertBatchAsync(entries, cancellationToken);
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

        return await metrics.InsertBatchAsync(samples, cancellationToken);
    }

    /// <summary>Shortens the value to at most <paramref name="maxLength"/> characters, ending in an ellipsis.</summary>
    public static string Truncate(string value, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length <= maxLength)
        {
            return value;
        }

        var cut = maxLength - 1;
        // Never split a surrogate pair (emoji and other non-BMP characters).
        if (char.IsHighSurrogate(value[cut - 1]))
        {
            cut--;
        }

        return string.Concat(value.AsSpan(0, cut), "…");
    }

    public const string UnknownSource = "unknown";

    /// <summary>
    /// A timestamp from the future would stay on top of the newest-first viewer and never be purged by retention,
    /// so anything beyond the allowed clock skew is replaced by the receive time.
    /// </summary>
    public static DateTimeOffset ClampFuture(DateTimeOffset timestamp, DateTimeOffset now) =>
        timestamp > now + IngestLimits.MaxClockSkew ? now : timestamp;
}
