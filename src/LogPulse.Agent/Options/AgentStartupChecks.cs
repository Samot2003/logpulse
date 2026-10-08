using Microsoft.Extensions.Options;

namespace LogPulse.Agent.Options;

public static class AgentStartupChecks
{
    /// <summary>Marker of the public development API key in appsettings.Development.json.</summary>
    public const string DevelopmentKeyMarker = "_not_secret";

    /// <summary>
    /// Validates the options before any service starts, and outside Development refuses the public development
    /// key and plain HTTP to a remote API (the API key and tokens would travel in clear text).
    /// </summary>
    public static void EnsureValidAgentConfiguration(this IHost host)
    {
        ArgumentNullException.ThrowIfNull(host);

        // Reading .Value runs the data-annotation validation and throws OptionsValidationException on bad settings.
        var options = host.Services.GetRequiredService<IOptions<AgentOptions>>().Value;
        Check(options, host.Services.GetRequiredService<IHostEnvironment>());
    }

    public static void Check(AgentOptions options, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);

        if (environment.IsDevelopment())
        {
            return;
        }

        if (options.ApiKey.EndsWith(DevelopmentKeyMarker, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Agent:ApiKey is the public development key, but the environment is '{environment.EnvironmentName}'. Configure the key created with POST /api/agents.");
        }

        if (options.ApiBaseUrl is { } url && url.Scheme == Uri.UriSchemeHttp && !url.IsLoopback && !options.AllowInsecureHttp)
        {
            throw new InvalidOperationException(
                $"Agent:ApiBaseUrl uses plain HTTP to a remote host ({url.Host}): the API key would travel unencrypted. Use https (or set Agent:AllowInsecureHttp on a private network).");
        }
    }
}
