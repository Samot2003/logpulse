using LogPulse.Api.Options;
using Microsoft.Extensions.Options;

namespace LogPulse.Api.Infrastructure;

public static class StartupChecks
{
    /// <summary>Marker of the public development signing key in appsettings.Development.json.</summary>
    public const string DevelopmentKeyMarker = "dev-only";

    /// <summary>
    /// Validates every options class before the database is touched, and refuses to run outside Development
    /// with the public development signing key (anyone could forge an Admin token with it).
    /// </summary>
    public static void EnsureValidConfiguration(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Reading .Value runs the data-annotation validation and throws OptionsValidationException on bad settings.
        var jwt = app.Services.GetRequiredService<IOptions<JwtOptions>>().Value;
        _ = app.Services.GetRequiredService<IOptions<RateLimitingOptions>>().Value;
        _ = app.Services.GetRequiredService<IOptions<RetentionOptions>>().Value;

        if (!app.Environment.IsDevelopment() && jwt.SigningKey.StartsWith(DevelopmentKeyMarker, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Jwt:SigningKey is the public development key, but the environment is '{app.Environment.EnvironmentName}'. Configure a real secret.");
        }
    }
}
