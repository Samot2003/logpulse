using System.Net;
using LogPulse.Agent;
using LogPulse.Agent.Auth;
using LogPulse.Agent.Shipping;
using LogPulse.Core.Contracts;
using LogPulse.Tests.Fakes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;

namespace LogPulse.Tests.Unit.Agent;

/// <summary>The HTTP pipelines as registered by AddLogPulseAgent: token handler, resilience handler, no network.</summary>
public sealed class AgentWiringTests : IDisposable
{
    private const string LoginPath = "/api/auth/agent-token";
    private const string RefreshPath = "/api/auth/refresh";
    private const string IngestPath = "/api/ingest/logs";

    private readonly string _dir = Directory.CreateTempSubdirectory("logpulse-wiring-").FullName;
    private readonly Queue<HttpStatusCode> _ingestStatuses = new();
    private readonly StubHttpHandler _api;
    private readonly ServiceProvider _provider;
    private HttpStatusCode _authStatus = HttpStatusCode.OK;
    private int _issued;

    public AgentWiringTests()
    {
        _api = new StubHttpHandler(Respond);

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Agent:ApiBaseUrl"] = "http://api.test",
            ["Agent:ServerName"] = "web-01",
            ["Agent:ApiKey"] = "lp_key",
            ["Agent:StateDirectory"] = _dir,
        }).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new HostingEnvironment { EnvironmentName = "Testing", ContentRootPath = _dir });
        services.AddLogPulseAgent(configuration);
        services.ConfigureHttpClientDefaults(http => http.ConfigurePrimaryHttpMessageHandler(() => _api));
        _provider = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _provider.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private HttpResponseMessage Respond(HttpRequestMessage request)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path is LoginPath or RefreshPath)
        {
            if (_authStatus != HttpStatusCode.OK)
            {
                return new HttpResponseMessage(_authStatus);
            }

            var n = Interlocked.Increment(ref _issued);
            return StubHttpHandler.Tokens($"access-{n}", $"refresh-{n}", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(15));
        }

        var status = _ingestStatuses.Count > 0 ? _ingestStatuses.Dequeue() : HttpStatusCode.OK;
        return status == HttpStatusCode.OK ? StubHttpHandler.Json(new IngestResult(1, ViewersOnline: true)) : new HttpResponseMessage(status);
    }

    private static IngestLogBatch Batch() => new() { Entries = [new IngestLogEntry { Source = "app", Message = "x" }] };

    [Fact]
    public async Task A_401_through_the_real_pipeline_renews_the_token_and_resends_the_batch()
    {
        _ingestStatuses.Enqueue(HttpStatusCode.Unauthorized);

        var result = await _provider.GetRequiredService<IIngestClient>().SendLogsAsync(Batch(), CancellationToken.None);

        Assert.Equal(SendOutcome.Accepted, result.Outcome);
        Assert.Equal(["access-1", "access-2"], _api.Requests.Where(r => r.Path == IngestPath).Select(r => r.BearerToken));
        Assert.Equal(1, _api.CountTo(RefreshPath));
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task Login_and_refresh_are_never_retried_automatically(HttpStatusCode status)
    {
        _authStatus = status;
        var auth = _provider.GetRequiredService<AgentAuthClient>();

        await Assert.ThrowsAsync<HttpRequestException>(() => auth.RefreshAsync("refresh-1", CancellationToken.None));
        await Assert.ThrowsAsync<HttpRequestException>(() => auth.LoginAsync(CancellationToken.None));

        Assert.Equal(1, _api.CountTo(RefreshPath));
        Assert.Equal(1, _api.CountTo(LoginPath));
    }
}
