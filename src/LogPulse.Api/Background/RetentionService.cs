using LogPulse.Api.Options;
using LogPulse.Data.Daos;
using Microsoft.Extensions.Options;

namespace LogPulse.Api.Background;

public sealed record RetentionResult(int LogsDeleted, int MetricsDeleted, int RefreshTokensDeleted);

/// <summary>Periodically purges old logs, old metrics and expired refresh tokens, in batches.</summary>
public sealed partial class RetentionService(
    ILogDao logs,
    IMetricDao metrics,
    IRefreshTokenDao refreshTokens,
    IOptions<RetentionOptions> options,
    TimeProvider time,
    ILogger<RetentionService> logger) : BackgroundService
{
    public async Task<RetentionResult> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        var now = time.GetUtcNow();

        var logsDeleted = await logs.DeleteOlderThanAsync(now.AddDays(-settings.LogRetentionDays), settings.BatchSize, cancellationToken);
        var metricsDeleted = await metrics.DeleteOlderThanAsync(now.AddDays(-settings.MetricRetentionDays), settings.BatchSize, cancellationToken);
        var tokensDeleted = await refreshTokens.DeleteExpiredAsync(now, settings.BatchSize, cancellationToken);

        LogPurged(logger, logsDeleted, metricsDeleted, tokensDeleted);
        return new RetentionResult(logsDeleted, metricsDeleted, tokensDeleted);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A failed purge (e.g. the database is briefly unreachable) must not stop the service; retry next cycle.
                LogPurgeFailed(logger, ex);
            }

            await Task.Delay(settings.Interval, time, stoppingToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Retention purge deleted {Logs} log entries, {Metrics} metric samples and {Tokens} expired refresh tokens")]
    private static partial void LogPurged(ILogger logger, int logs, int metrics, int tokens);

    [LoggerMessage(Level = LogLevel.Error, Message = "Retention purge failed; it will be retried in the next cycle")]
    private static partial void LogPurgeFailed(ILogger logger, Exception exception);
}
