using LogPulse.Data;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace LogPulse.Api.Infrastructure;

/// <summary>Healthy when a database connection can be opened. Details stay in the logs, not in the response.</summary>
public sealed class DatabaseHealthCheck(IDbConnectionFactory connectionFactory) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            await using var connection = await connectionFactory.OpenAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, "Database unreachable", ex);
        }
    }
}
