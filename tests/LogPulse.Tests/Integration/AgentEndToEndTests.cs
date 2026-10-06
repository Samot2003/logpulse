using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using LogPulse.Agent;
using LogPulse.Agent.Logs;
using LogPulse.Agent.Options;
using LogPulse.Core.Contracts;
using LogPulse.Core.Models;
using LogPulse.Core.Queries;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace LogPulse.Tests.Integration;

/// <summary>The real agent host (tailer, sampler, sender, token handling) against the real API and SQL Server.</summary>
[Collection(SqlServerGroup.Name)]
[Trait("Category", "Integration")]
public sealed class AgentEndToEndTests(SqlServerFixture fixture) : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    private readonly string _dir = Directory.CreateTempSubdirectory("logpulse-agent-it-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private async Task<HttpClient> LoginAsync(string userName, string password)
    {
        var client = fixture.Api.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/token", new UserTokenRequest { UserName = userName, Password = password });
        response.EnsureSuccessStatusCode();
        var token = (await response.Content.ReadFromJsonAsync<TokenResponse>(Json))!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        return client;
    }

    private IHost BuildAgent(string serverName, string apiKey)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Testing", ContentRootPath = _dir });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Agent:ApiBaseUrl"] = fixture.Api.Server.BaseAddress.ToString(),
            ["Agent:ServerName"] = serverName,
            ["Agent:ApiKey"] = apiKey,
            ["Agent:MetricsInterval"] = "00:00:00.2",
            ["Agent:IdleMetricsInterval"] = "00:00:00.2",
            ["Agent:SendInterval"] = "00:00:00.2",
            ["Agent:LogPollInterval"] = "00:00:00.1",
            ["Agent:ReadExistingLogs"] = "true",
            ["Agent:LogFiles:0:Path"] = "app.log",
            ["Agent:LogFiles:0:Source"] = "it-app",
        });
        builder.Services.AddLogPulseAgent(builder.Configuration);

        // Every request of the agent goes to the in-memory API instead of the network.
        builder.Services.ConfigureHttpClientDefaults(http => http.ConfigurePrimaryHttpMessageHandler(() => fixture.Api.Server.CreateHandler()));
        return builder.Build();
    }

    private static async Task<T> EventuallyAsync<T>(Func<Task<T?>> probe)
        where T : class
    {
        var started = DateTime.UtcNow;
        while (true)
        {
            if (await probe() is { } value)
            {
                return value;
            }

            Assert.True(DateTime.UtcNow - started < Deadline, "The agent's data did not reach the API in time.");
            await Task.Delay(200);
        }
    }

    [Fact]
    public async Task A_running_agent_appears_with_its_log_lines_and_metrics()
    {
        using var admin = await LoginAsync(ApiFactory.AdminUser, ApiFactory.AdminPassword);
        var serverName = $"it-agent-{Guid.NewGuid():N}";
        var created = await admin.PostAsJsonAsync("/api/agents", new CreateAgentRequest { ServerName = serverName });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var apiKey = (await created.Content.ReadFromJsonAsync<CreateAgentResponse>(Json))!.ApiKey;

        var logFile = Path.Combine(_dir, "app.log");
        File.WriteAllText(logFile, "2026-10-05 12:00:00 INFO service started\n2026-10-05 12:00:01 ERROR payment timeout\n");

        using var agent = BuildAgent(serverName, apiKey);
        agent.EnsureValidAgentConfiguration();
        await agent.StartAsync();

        using var viewer = await LoginAsync(ApiFactory.ViewerUser, ApiFactory.ViewerPassword);
        var server = await EventuallyAsync(async () =>
            (await viewer.GetFromJsonAsync<List<Server>>("/api/servers", Json))!.SingleOrDefault(s => s.Name == serverName));

        // A line written while the agent runs is picked up too.
        File.AppendAllText(logFile, "2026-10-05 12:00:02 WARN queue is growing\n");

        var logs = await EventuallyAsync(async () =>
        {
            var page = await viewer.GetFromJsonAsync<PagedResult<LogEntry>>($"/api/logs?serverId={server.Id}", Json);
            return page!.TotalCount >= 3 ? page.Items : null;
        });
        var latest = await EventuallyAsync(async () =>
            (await viewer.GetFromJsonAsync<List<MetricSample>>("/api/metrics/latest", Json))!.SingleOrDefault(m => m.ServerId == server.Id));

        await agent.StopAsync();

        Assert.Equal(3, logs.Count);
        Assert.All(logs, l => Assert.Equal("it-app", l.Source));
        Assert.Equal(
            [LogSeverity.Warning, LogSeverity.Error, LogSeverity.Information],
            logs.OrderByDescending(l => l.Id).Select(l => l.Severity));
        Assert.True(latest.MemoryTotalMb > 0);

        // The confirmed position is saved, so a restart would not send these lines again.
        var positions = new CheckpointStore(Path.Combine(_dir, "state"), NullLogger<CheckpointStore>.Instance);
        Assert.Equal(new FileInfo(logFile).Length, positions.Find(logFile)?.Offset);
    }
}
