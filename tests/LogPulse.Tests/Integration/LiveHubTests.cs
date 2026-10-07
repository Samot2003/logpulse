using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using LogPulse.Core.Contracts;
using LogPulse.Core.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace LogPulse.Tests.Integration;

/// <summary>The live hub and the rate limits added with the dashboard, through the real API and SQL Server.</summary>
[Collection(SqlServerGroup.Name)]
[Trait("Category", "Integration")]
public class LiveHubTests(SqlServerFixture fixture)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);

    private static async Task<TokenResponse> TokenAsync(HttpClient client, string path, object request)
    {
        var response = await client.PostAsJsonAsync(path, request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TokenResponse>(Json))!;
    }

    private Task<TokenResponse> ViewerTokenAsync() =>
        TokenAsync(fixture.Api.CreateClient(), "/api/auth/token", new UserTokenRequest { UserName = ApiFactory.ViewerUser, Password = ApiFactory.ViewerPassword });

    private Task<TokenResponse> AgentTokenAsync() =>
        TokenAsync(fixture.Api.CreateClient(), "/api/auth/agent-token", new AgentTokenRequest { ServerName = ApiFactory.SeededAgentServer, ApiKey = ApiFactory.SeededAgentKey });

    private HubConnection Connect(string? token) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(fixture.Api.Server.BaseAddress, LiveHubRoute.Path.TrimStart('/')), http =>
            {
                http.HttpMessageHandlerFactory = _ => fixture.Api.Server.CreateHandler();
                http.Transports = HttpTransportType.LongPolling; // the in-memory server has no real sockets
                http.AccessTokenProvider = () => Task.FromResult(token);
            })
            .Build();

    private async Task<IngestResult> IngestAsync(TokenResponse agent, object batch, string path)
    {
        using var client = fixture.Api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", agent.AccessToken);
        var response = await client.PostAsJsonAsync(path, batch, Json);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<IngestResult>(Json))!;
    }

    private static async Task EventuallyAsync(Func<Task<bool>> condition)
    {
        var started = DateTime.UtcNow;
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow - started < Deadline, "The condition was not met in time.");
            await Task.Delay(100);
        }
    }

    [Fact]
    public async Task A_connected_viewer_receives_new_data_and_agents_learn_that_someone_is_watching()
    {
        var agent = await AgentTokenAsync();
        var logs = new TaskCompletionSource<LogsReceivedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var metrics = new TaskCompletionSource<MetricsReceivedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var now = DateTimeOffset.UtcNow;
        var logBatch = new IngestLogBatch
        {
            Entries =
            [
                new IngestLogEntry { Timestamp = now, Severity = LogSeverity.Warning, Source = "app", Message = "slow" },
                new IngestLogEntry { Timestamp = now, Severity = LogSeverity.Error, Source = "app", Message = "failed" },
            ],
        };
        var metricBatch = new IngestMetricBatch { Samples = [new IngestMetricSample { Timestamp = now, CpuPercent = 42, MemoryUsedMb = 1, MemoryTotalMb = 2 }] };

        await using (var viewer = Connect((await ViewerTokenAsync()).AccessToken))
        {
            viewer.On<LogsReceivedEvent>(nameof(ILiveClient.LogsReceived), e => logs.TrySetResult(e));
            viewer.On<MetricsReceivedEvent>(nameof(ILiveClient.MetricsReceived), e => metrics.TrySetResult(e));
            await viewer.StartAsync();

            var logResult = await IngestAsync(agent, logBatch, "/api/ingest/logs");
            var metricResult = await IngestAsync(agent, metricBatch, "/api/ingest/metrics");

            Assert.True(logResult.ViewersOnline);
            Assert.True(metricResult.ViewersOnline);
            var logEvent = await logs.Task.WaitAsync(Deadline);
            Assert.Equal((ApiFactory.SeededAgentServer, 2, LogSeverity.Error), (logEvent.ServerName, logEvent.Count, logEvent.MaxSeverity));
            var metricEvent = await metrics.Task.WaitAsync(Deadline);
            Assert.Equal(logEvent.ServerId, metricEvent.ServerId);
            Assert.Equal(42, Assert.Single(metricEvent.Samples).CpuPercent);
        }

        // Once the viewer leaves, agents are told nobody is watching (the server notices the disconnect shortly after).
        await EventuallyAsync(async () => !(await IngestAsync(agent, metricBatch, "/api/ingest/metrics")).ViewersOnline);
    }

    [Fact]
    public async Task Only_dashboard_users_can_connect()
    {
        await using var anonymous = Connect(null);
        var refused = await Assert.ThrowsAsync<HttpRequestException>(() => anonymous.StartAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);

        await using var agent = Connect((await AgentTokenAsync()).AccessToken);
        var forbidden = await Assert.ThrowsAsync<HttpRequestException>(() => agent.StartAsync());
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    [Fact]
    public async Task Ingestion_is_rate_limited_per_agent_with_a_retry_after()
    {
        await using var api = new ApiFactory(fixture.ConnectionString, extraSettings: new Dictionary<string, string?> { ["RateLimiting:IngestPermitsPerMinute"] = "2" });
        using var client = api.CreateClient();
        var agent = await TokenAsync(client, "/api/auth/agent-token", new AgentTokenRequest { ServerName = ApiFactory.SeededAgentServer, ApiKey = ApiFactory.SeededAgentKey });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", agent.AccessToken);
        var batch = new IngestMetricBatch { Samples = [new IngestMetricSample { Timestamp = DateTimeOffset.UtcNow, CpuPercent = 1 }] };

        var statuses = new List<HttpStatusCode>();
        HttpResponseMessage? last = null;
        for (var i = 0; i < 3; i++)
        {
            last = await client.PostAsJsonAsync("/api/ingest/metrics", batch, Json);
            statuses.Add(last.StatusCode);
        }

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests], statuses);
        Assert.InRange(int.Parse(Assert.Single(last!.Headers.GetValues("Retry-After")), System.Globalization.CultureInfo.InvariantCulture), 1, 60);
    }

    [Fact]
    public async Task Queries_are_rate_limited_per_user()
    {
        await using var api = new ApiFactory(fixture.ConnectionString, extraSettings: new Dictionary<string, string?> { ["RateLimiting:ReadPermitsPerMinute"] = "2" });
        async Task<HttpClient> AsUser(string user, string password)
        {
            var client = api.CreateClient();
            var token = await TokenAsync(client, "/api/auth/token", new UserTokenRequest { UserName = user, Password = password });
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
            return client;
        }

        using var viewer = await AsUser(ApiFactory.ViewerUser, ApiFactory.ViewerPassword);
        using var admin = await AsUser(ApiFactory.AdminUser, ApiFactory.AdminPassword);

        var viewerStatuses = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
        {
            viewerStatuses.Add((await viewer.GetAsync("/api/servers")).StatusCode);
        }

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests], viewerStatuses);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/servers")).StatusCode); // another user, another budget
    }

    [Fact]
    public async Task Behind_a_trusted_proxy_logins_are_limited_per_forwarded_client()
    {
        // The in-memory server has no remote address; pretend every request comes from a proxy on the same machine.
        await using var api = new ApiFactory(
            fixture.ConnectionString,
            authPermitsPerMinute: 2,
            configure: host => host.ConfigureTestServices(services => services.AddSingleton<IStartupFilter, FromLoopback>()));
        using var client = api.CreateClient();
        async Task<HttpStatusCode> LoginFrom(string clientAddress)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/token")
            {
                Content = JsonContent.Create(new UserTokenRequest { UserName = "ghost", Password = "nope" }),
            };
            request.Headers.Add("X-Forwarded-For", clientAddress);
            return (await client.SendAsync(request)).StatusCode;
        }

        Assert.Equal(HttpStatusCode.Unauthorized, await LoginFrom("203.0.113.1"));
        Assert.Equal(HttpStatusCode.Unauthorized, await LoginFrom("203.0.113.1"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await LoginFrom("203.0.113.1"));
        Assert.Equal(HttpStatusCode.Unauthorized, await LoginFrom("203.0.113.2"));
    }

    private sealed class FromLoopback : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Loopback;
                return nextMiddleware(context);
            });
            next(app);
        };
    }
}
