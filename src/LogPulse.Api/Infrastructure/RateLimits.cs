using System.Globalization;
using System.Threading.RateLimiting;
using LogPulse.Api.Options;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace LogPulse.Api.Infrastructure;

public static class RateLimits
{
    /// <summary>
    /// Fixed one-minute windows: per client IP for the anonymous auth endpoints (brute force), per agent for
    /// ingestion and per user for queries. Rejections are 429 with a Retry-After header.
    /// </summary>
    public static IServiceCollection AddLogPulseRateLimits(this IServiceCollection services) =>
        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.OnRejected = (context, _) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter =
                        Math.Ceiling(retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);
                }

                return ValueTask.CompletedTask;
            };

            limiter.AddPolicy(Policies.AuthRateLimit, context =>
                PerMinute(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", Settings(context).AuthPermitsPerMinute));
            limiter.AddPolicy(Policies.IngestRateLimit, context => PerCaller(context, Settings(context).IngestPermitsPerMinute));
            limiter.AddPolicy(Policies.ReadRateLimit, context => PerCaller(context, Settings(context).ReadPermitsPerMinute));
        });

    private static RateLimitingOptions Settings(HttpContext context) =>
        context.RequestServices.GetRequiredService<IOptions<RateLimitingOptions>>().Value;

    // The limiter runs after authentication and before authorization. A caller without a valid token is not
    // limited here because authorization rejects it right after, without touching the database.
    private static RateLimitPartition<string> PerCaller(HttpContext context, int permits) =>
        context.User.Identity is { IsAuthenticated: true, Name: { Length: > 0 } name }
            ? PerMinute(name, permits)
            : RateLimitPartition.GetNoLimiter(string.Empty);

    private static RateLimitPartition<string> PerMinute(string key, int permits) =>
        RateLimitPartition.GetFixedWindowLimiter(
            key, _ => new FixedWindowRateLimiterOptions { PermitLimit = permits, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });
}
