using System.ComponentModel.DataAnnotations;
using LogPulse.Data.Daos;

namespace LogPulse.Api.Options;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    [Required]
    public string Issuer { get; set; } = "logpulse";

    [Required]
    public string Audience { get; set; } = "logpulse";

    /// <summary>HMAC-SHA256 key. At least 32 characters (256 bits); never committed with a real value.</summary>
    [Required]
    [MinLength(32)]
    public string SigningKey { get; set; } = string.Empty;

    [Range(1, 1440)]
    public int AccessTokenMinutes { get; set; } = 15;

    /// <summary>Lifetime of one refresh token; each refresh issues a new one.</summary>
    [Range(1, 90)]
    public int RefreshTokenDays { get; set; } = 7;

    /// <summary>Absolute lifetime of a session: after this many days since login, refreshing stops working.</summary>
    [Range(1, 365)]
    public int MaxSessionDays { get; set; } = 30;
}

public sealed class RateLimitingOptions
{
    public const string SectionName = "RateLimiting";

    /// <summary>Requests per minute and client IP to the /api/auth endpoints.</summary>
    [Range(1, 10_000)]
    public int AuthPermitsPerMinute { get; set; } = 10;
}

public sealed class RetentionOptions
{
    public const string SectionName = "Retention";

    public bool Enabled { get; set; } = true;

    /// <summary>Time between purges. Zero or negative values would spin or crash the service, so they are rejected.</summary>
    [Range(typeof(TimeSpan), "00:01:00", "7.00:00:00")]
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(1);

    [Range(1, 3650)]
    public int LogRetentionDays { get; set; } = 30;

    [Range(1, 3650)]
    public int MetricRetentionDays { get; set; } = 7;

    /// <summary>Rows per DELETE; capped below SQL Server's lock escalation threshold.</summary>
    [Range(1, DataDefaults.MaxDeleteBatchSize)]
    public int BatchSize { get; set; } = DataDefaults.DeleteBatchSize;
}

/// <summary>Users and agent keys created at startup if missing. Only for development and demos.</summary>
public sealed class SeedOptions
{
    public const string SectionName = "Seed";

    public List<SeedUser> Users { get; set; } = [];

    public List<SeedAgent> Agents { get; set; } = [];
}

public sealed class SeedUser
{
    public string UserName { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
}

public sealed class SeedAgent
{
    public string ServerName { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
}
