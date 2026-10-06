using LogPulse.Agent.Auth;
using LogPulse.Agent.Logs;
using LogPulse.Agent.Metrics;
using LogPulse.Agent.Options;
using LogPulse.Agent.Shipping;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace LogPulse.Agent;

public static class AgentServiceCollectionExtensions
{
    /// <summary>Registers the agent: collectors, buffers, the authenticated API clients and the background services.</summary>
    public static IServiceCollection AddLogPulseAgent(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<AgentOptions>()
            .Bind(configuration.GetSection(AgentOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ViewerActivity>();
        services.AddSingleton<AgentBuffers>();
        services.AddSingleton(sp => new CheckpointStore(
            Path.GetFullPath(sp.GetRequiredService<IOptions<AgentOptions>>().Value.StateDirectory, sp.GetRequiredService<IHostEnvironment>().ContentRootPath),
            sp.GetRequiredService<ILogger<CheckpointStore>>()));
        services.AddSingleton(_ => CreateMetricsCollector());

        // Clients are created per request from IHttpClientFactory, so connections are recycled (DNS changes are
        // picked up) even though the services using them are singletons.
        services.AddSingleton<AgentAuthClient>();
        services.AddSingleton<AgentTokenProvider>();
        services.AddSingleton<IIngestClient, IngestClient>();
        services.AddTransient<AuthTokenHandler>();

        // Login and refresh: a short timeout and no retries (see AgentAuthClient).
        services.AddHttpClient(AgentAuthClient.HttpClientName, (sp, http) =>
            {
                ConfigureApiClient(sp, http);
                http.Timeout = TimeSpan.FromSeconds(15);
            })
            .RedactLoggedHeaders(["Authorization"]);

        // Ingestion: bearer token (outermost, so a 401 is handled after the retries), then the standard resilience
        // pipeline: retries with exponential backoff and jitter, per-attempt and total timeouts, circuit breaker.
        services.AddHttpClient(IngestClient.HttpClientName, (sp, http) =>
            {
                ConfigureApiClient(sp, http);
                // Above the worst case of the resilience pipeline below (2 min, twice after a 401, plus the token
                // renewal), which HttpClient's default 100 s would cut short. Still finite: reading the response
                // body happens outside the pipeline and must not hang forever on a dead connection.
                http.Timeout = TimeSpan.FromMinutes(5);
            })
            .RedactLoggedHeaders(["Authorization"])
            .AddHttpMessageHandler<AuthTokenHandler>()
            .AddStandardResilienceHandler(resilience =>
            {
                // A batch of up to MaxBatchBytes must fit in one attempt on a slow uplink (the default is 10 s).
                resilience.AttemptTimeout.Timeout = TimeSpan.FromSeconds(30);
                resilience.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(2);
                // Must be at least twice the attempt timeout.
                resilience.CircuitBreaker.SamplingDuration = TimeSpan.FromMinutes(1);
            });

        services.AddHostedService<LogTailService>();
        services.AddHostedService<MetricsSampler>();
        services.AddHostedService<IngestSender>();
        return services;
    }

    private static void ConfigureApiClient(IServiceProvider services, HttpClient http)
    {
        // [Required] and ValidateOnStart guarantee a value before any client is created.
        var url = services.GetRequiredService<IOptions<AgentOptions>>().Value.ApiBaseUrl!;
        // With a trailing slash, relative endpoints resolve under a base path: https://host/logpulse/ + api/... .
        http.BaseAddress = url.AbsoluteUri.EndsWith('/') ? url : new Uri(url.AbsoluteUri + "/");
    }

    private static IMetricsCollector CreateMetricsCollector()
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsMetricsCollector();
        }

        if (OperatingSystem.IsLinux())
        {
            return new LinuxMetricsCollector();
        }

        throw new PlatformNotSupportedException("The LogPulse agent collects metrics on Windows and Linux only.");
    }
}
