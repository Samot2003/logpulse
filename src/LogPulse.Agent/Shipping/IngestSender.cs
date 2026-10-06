using LogPulse.Agent.Logs;
using LogPulse.Agent.Options;
using LogPulse.Core.Contracts;
using Microsoft.Extensions.Options;

namespace LogPulse.Agent.Shipping;

/// <summary>
/// Every <see cref="AgentOptions.SendInterval"/>, sends what is buffered, batch by batch. A batch that fails for a
/// temporary reason is kept and sent again first, with exponential backoff, so lines keep their order and a
/// position is only saved after the API has stored everything before it. Metrics go before every log batch, so a
/// busy or stuck log never hides the server's metrics.
/// </summary>
public sealed partial class IngestSender(
    AgentBuffers buffers,
    IIngestClient client,
    CheckpointStore checkpoints,
    ViewerActivity viewers,
    IOptions<AgentOptions> options,
    TimeProvider time,
    ILogger<IngestSender> logger) : BackgroundService
{
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(1);

    // Log batches taken from the buffer but not confirmed yet, next one on top. Usually zero or one; more after a
    // 413, when a batch is split in halves.
    private readonly Stack<List<PendingLog>> _pendingLogs = new();
    private List<IngestMetricSample>? _metricBatch;
    private int _failures;

    /// <summary>
    /// Failed rounds (a batch to retry or an unexpected error) since logs last went through; drives the backoff.
    /// </summary>
    public int ConsecutiveFailures => _failures;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = options.Value.SendInterval;
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(NextDelay(interval), time, stoppingToken);
            try
            {
                await SendPendingAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Unexpected (a bug, not an outage): logged every time, counted as a failed attempt so the backoff
                // applies, and the pending batches are kept instead of stopping the agent.
                _failures++;
                LogSendFailed(logger, ex);
            }
        }
    }

    /// <summary>Sends everything buffered. Returns false when it stopped at a batch that has to be retried.</summary>
    public async Task<bool> SendPendingAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        while (true)
        {
            if (!await SendMetricsAsync(settings.MaxBatchItems, cancellationToken))
            {
                return false;
            }

            if (_pendingLogs.Count == 0)
            {
                var next = BatchBuilder.TakeLogs(buffers.Logs.Reader, settings.MaxBatchItems, settings.MaxBatchBytes);
                if (next.Count == 0)
                {
                    MarkRecovered();
                    return true;
                }

                _pendingLogs.Push(next);
            }

            var batch = _pendingLogs.Peek();
            var result = await client.SendLogsAsync(new IngestLogBatch { Entries = batch.ConvertAll(p => p.Entry) }, cancellationToken);
            if (result.Outcome == SendOutcome.TooLarge && batch.Count > 1)
            {
                // Too big for the API or a proxy in front of it: send the first half, then the second.
                _pendingLogs.Pop();
                _pendingLogs.Push(batch.GetRange(batch.Count / 2, batch.Count - (batch.Count / 2)));
                _pendingLogs.Push(batch.GetRange(0, batch.Count / 2));
                LogSplitting(logger, batch.Count);
                continue;
            }

            if (!Handle(result, "log entries", batch.Count))
            {
                return false;
            }

            // Stored, or refused for good: either way these lines are done, so their position can be saved.
            _pendingLogs.Pop();
            SavePositions(batch);
            if (result.Outcome == SendOutcome.Accepted)
            {
                MarkRecovered();
            }
        }
    }

    // Only logs going through (or everything sent) ends an outage: metrics are sent first in every round, so
    // their success alone would reset the backoff while a log batch keeps failing.
    private void MarkRecovered()
    {
        if (_failures > 0)
        {
            LogRecovered(logger, _failures);
            _failures = 0;
        }
    }

    private async Task<bool> SendMetricsAsync(int maxItems, CancellationToken cancellationToken)
    {
        while ((_metricBatch ??= BatchBuilder.TakeMetrics(buffers.Metrics.Reader, maxItems)).Count > 0)
        {
            var batch = new IngestMetricBatch { Samples = _metricBatch };
            if (!Handle(await client.SendMetricsAsync(batch, cancellationToken), "metric samples", _metricBatch.Count))
            {
                return false;
            }

            _metricBatch = null;
        }

        _metricBatch = null;
        return true;
    }

    private bool Handle(SendResult result, string kind, int count)
    {
        switch (result.Outcome)
        {
            case SendOutcome.Accepted:
                viewers.Report(result.ViewersOnline);
                return true;

            case SendOutcome.Rejected or SendOutcome.TooLarge:
                // Retrying would fail forever and block everything behind it; dropping is the only way forward.
                // (A log batch only gets here as TooLarge when it is down to a single entry.)
                LogRejected(logger, count, kind, result.Detail);
                return true;

            default:
                // Logged once per outage, not on every attempt.
                if (_failures++ == 0)
                {
                    LogRetrying(logger, kind, result.Detail);
                }

                return false;
        }
    }

    private void SavePositions(List<PendingLog> batch)
    {
        try
        {
            checkpoints.Save(batch.Select(p => p.Position));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The lines are stored; at worst they are sent again after a restart.
            LogPositionsNotSaved(logger, ex);
        }
    }

    private TimeSpan NextDelay(TimeSpan interval) =>
        _failures == 0 ? interval : TimeSpan.FromTicks(Math.Min(interval.Ticks << Math.Min(_failures, 16), MaxBackoff.Ticks));

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not send {Kind} ({Reason}); keeping them and retrying with backoff")]
    private static partial void LogRetrying(ILogger logger, string kind, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Sending failed unexpectedly; keeping the data and retrying with backoff")]
    private static partial void LogSendFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "The API is reachable again after {Failures} failed attempt(s)")]
    private static partial void LogRecovered(ILogger logger, int failures);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A batch of {Count} log entries was too large for the API or a proxy (413); sending it in halves. Consider lowering Agent:MaxBatchBytes")]
    private static partial void LogSplitting(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "The API rejected {Count} {Kind} ({Reason}); they are dropped")]
    private static partial void LogRejected(ILogger logger, int count, string kind, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not save the log positions; some lines may be sent again after a restart")]
    private static partial void LogPositionsNotSaved(ILogger logger, Exception exception);
}
