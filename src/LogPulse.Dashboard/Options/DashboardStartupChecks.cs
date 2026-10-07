using Microsoft.Extensions.Options;

namespace LogPulse.Dashboard.Options;

public static class DashboardStartupChecks
{
    /// <summary>
    /// Validates the options before the dashboard starts, and outside Development refuses plain HTTP to a remote
    /// API: user passwords and tokens would travel in clear text.
    /// </summary>
    public static void EnsureValidDashboardConfiguration(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Reading .Value runs the data-annotation validation and throws OptionsValidationException on bad settings.
        Check(app.Services.GetRequiredService<IOptions<DashboardOptions>>().Value, app.Environment);
    }

    public static void Check(DashboardOptions options, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);

        if (!environment.IsDevelopment() && options.ApiBaseUrl is { } url && url.Scheme == Uri.UriSchemeHttp && !url.IsLoopback)
        {
            throw new InvalidOperationException(
                $"Dashboard:ApiBaseUrl uses plain HTTP to a remote host ({url.Host}): passwords and tokens would travel unencrypted. Use https.");
        }
    }
}
