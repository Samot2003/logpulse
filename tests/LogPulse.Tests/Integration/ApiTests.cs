using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using LogPulse.Core.Contracts;
using LogPulse.Core.Models;
using LogPulse.Core.Queries;
using MessagePack;
using MessagePack.Resolvers;
using Microsoft.AspNetCore.Mvc;

namespace LogPulse.Tests.Integration;

/// <summary>End-to-end tests through HTTP: routing, auth, validation, formatters and SQL Server together.</summary>
[Collection(SqlServerGroup.Name)]
[Trait("Category", "Integration")]
public class ApiTests(SqlServerFixture fixture)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static readonly MessagePackSerializerOptions MessagePack = MessagePackSerializerOptions.Standard
        .WithResolver(ContractlessStandardResolver.Instance);

    private HttpClient NewClient() => fixture.Api.CreateClient();

    private static async Task<TokenResponse> LoginAsync(HttpClient client, string userName, string password)
    {
        var response = await client.PostAsJsonAsync("/api/auth/token", new UserTokenRequest { UserName = userName, Password = password });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TokenResponse>(Json))!;
    }

    private static HttpClient As(HttpClient client, TokenResponse token)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        return client;
    }

    /// <summary>Creates a fresh agent through the admin endpoint and returns its name and token pair.</summary>
    private async Task<(string Server, TokenResponse Token)> NewAgentAsync()
    {
        using var admin = As(NewClient(), await LoginAsync(NewClient(), ApiFactory.AdminUser, ApiFactory.AdminPassword));
        var server = $"it-{Guid.NewGuid():N}";
        var created = await admin.PostAsJsonAsync("/api/agents", new CreateAgentRequest { ServerName = server });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var key = (await created.Content.ReadFromJsonAsync<CreateAgentResponse>(Json))!.ApiKey;

        var token = await NewClient().PostAsJsonAsync("/api/auth/agent-token", new AgentTokenRequest { ServerName = server, ApiKey = key });
        token.EnsureSuccessStatusCode();
        return (server, (await token.Content.ReadFromJsonAsync<TokenResponse>(Json))!);
    }

    private async Task<HttpClient> ViewerAsync() =>
        As(NewClient(), await LoginAsync(NewClient(), ApiFactory.ViewerUser, ApiFactory.ViewerPassword));

    private static async Task<int> ServerIdAsync(HttpClient viewer, string name) =>
        (await viewer.GetFromJsonAsync<List<Server>>("/api/servers", Json))!.Single(s => s.Name == name).Id;

    [Fact]
    public async Task Health_is_anonymous_and_reports_the_database_as_healthy()
    {
        var response = await NewClient().GetAsync(new Uri("/health", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Swagger_is_not_exposed_outside_development()
    {
        // Anonymous callers get 401 for any unknown route (fallback policy), so ask as a signed-in user.
        using var viewer = await ViewerAsync();

        var response = await viewer.GetAsync(new Uri("/swagger/v1/swagger.json", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_routes_do_not_reveal_whether_they_exist_to_anonymous_callers()
    {
        var response = await NewClient().GetAsync(new Uri("/api/does-not-exist", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Wrong_password_and_unknown_user_get_the_same_401_problem()
    {
        var client = NewClient();

        var wrongPassword = await client.PostAsJsonAsync("/api/auth/token", new UserTokenRequest { UserName = ApiFactory.ViewerUser, Password = "nope" });
        var unknownUser = await client.PostAsJsonAsync("/api/auth/token", new UserTokenRequest { UserName = "ghost", Password = "nope" });

        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownUser.StatusCode);
        var a = await wrongPassword.Content.ReadFromJsonAsync<ProblemDetails>(Json);
        var b = await unknownUser.Content.ReadFromJsonAsync<ProblemDetails>(Json);
        Assert.Equal(a!.Title, b!.Title);
    }

    [Fact]
    public async Task The_seeded_agent_can_authenticate_and_a_wrong_key_cannot()
    {
        var client = NewClient();

        var ok = await client.PostAsJsonAsync("/api/auth/agent-token", new AgentTokenRequest { ServerName = ApiFactory.SeededAgentServer, ApiKey = ApiFactory.SeededAgentKey });
        var bad = await client.PostAsJsonAsync("/api/auth/agent-token", new AgentTokenRequest { ServerName = ApiFactory.SeededAgentServer, ApiKey = "lp_wrong" });

        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
    }

    [Fact]
    public async Task Each_endpoint_only_accepts_its_role()
    {
        var (_, agentToken) = await NewAgentAsync();
        using var agent = As(NewClient(), agentToken);
        using var viewer = await ViewerAsync();
        using var anonymous = NewClient();
        var batch = new IngestLogBatch { Entries = [new IngestLogEntry { Timestamp = DateTimeOffset.UtcNow, Source = "s", Message = "m" }] };

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(new Uri("/api/logs", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.GetAsync(new Uri("/api/logs", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync(new Uri("/api/logs", UriKind.Relative))).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/ingest/logs", batch)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync("/api/ingest/logs", batch)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await agent.PostAsJsonAsync("/api/ingest/logs", batch)).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync("/api/agents", new CreateAgentRequest { ServerName = "x" })).StatusCode);

        using var forged = NewClient();
        forged.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", agentToken.AccessToken[..^4] + "AAAA");
        Assert.Equal(HttpStatusCode.Unauthorized, (await forged.GetAsync(new Uri("/api/servers", UriKind.Relative))).StatusCode);
    }

    [Fact]
    public async Task Logs_ingested_as_json_are_stored_for_the_agents_server_and_can_be_queried()
    {
        var (server, token) = await NewAgentAsync();
        using var agent = As(NewClient(), token);
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-10);
        var batch = new IngestLogBatch
        {
            Entries =
            [
                new IngestLogEntry { Timestamp = t0, Severity = LogSeverity.Information, Source = "app", Message = "started" },
                new IngestLogEntry { Timestamp = t0.AddMinutes(1), Severity = LogSeverity.Error, Source = "app", Message = "disk at 100% on C:" },
                new IngestLogEntry { Timestamp = t0.AddMinutes(2), Severity = LogSeverity.Critical, Source = "app", Message = "crashed", Exception = "System.Exception: boom" },
            ],
        };

        var ingest = await agent.PostAsJsonAsync("/api/ingest/logs", batch, Json);
        // Nobody tracks dashboard connections yet, so agents are always told to keep the full rate.
        Assert.Equal(new IngestResult(3, ViewersOnline: true), await ingest.Content.ReadFromJsonAsync<IngestResult>(Json));

        using var viewer = await ViewerAsync();
        var serverId = await ServerIdAsync(viewer, server);
        var errors = await viewer.GetFromJsonAsync<PagedResult<LogEntry>>($"/api/logs?serverId={serverId}&minSeverity=Error", Json);
        Assert.Equal(2, errors!.TotalCount);
        Assert.Equal(["crashed", "disk at 100% on C:"], errors.Items.Select(e => e.Message));

        var search = await viewer.GetFromJsonAsync<PagedResult<LogEntry>>($"/api/logs?serverId={serverId}&search={Uri.EscapeDataString("100%")}", Json);
        Assert.Equal("disk at 100% on C:", Assert.Single(search!.Items).Message);
    }

    [Fact]
    public async Task Logs_and_metrics_can_be_ingested_as_messagepack()
    {
        var (server, token) = await NewAgentAsync();
        using var agent = As(NewClient(), token);
        var now = DateTimeOffset.UtcNow;

        var logs = new IngestLogBatch { Entries = [new IngestLogEntry { Timestamp = now, Severity = LogSeverity.Warning, Source = "agent", Message = "via msgpack" }] };
        var metrics = new IngestMetricBatch { Samples = [new IngestMetricSample { Timestamp = now, CpuPercent = 12.5, MemoryUsedMb = 2048, MemoryTotalMb = 8192, DiskUsedPercent = 40 }] };

        var logResponse = await agent.PostAsync(new Uri("/api/ingest/logs", UriKind.Relative), MessagePackContent(logs));
        var metricResponse = await agent.PostAsync(new Uri("/api/ingest/metrics", UriKind.Relative), MessagePackContent(metrics));

        Assert.Equal(HttpStatusCode.OK, logResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, metricResponse.StatusCode);

        using var viewer = await ViewerAsync();
        var serverId = await ServerIdAsync(viewer, server);
        var stored = await viewer.GetFromJsonAsync<PagedResult<LogEntry>>($"/api/logs?serverId={serverId}", Json);
        Assert.Equal("via msgpack", Assert.Single(stored!.Items).Message);
        var samples = await viewer.GetFromJsonAsync<List<MetricSample>>($"/api/metrics/{serverId}", Json);
        Assert.Equal(12.5, Assert.Single(samples!).CpuPercent);
        var latest = await viewer.GetFromJsonAsync<List<MetricSample>>("/api/metrics/latest", Json);
        Assert.Contains(latest!, s => s.ServerId == serverId && s.CpuPercent == 12.5);
    }

    private static ByteArrayContent MessagePackContent<T>(T value)
    {
        var content = new ByteArrayContent(MessagePackSerializer.Serialize(value, MessagePack));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-msgpack");
        return content;
    }

    [Fact]
    public async Task Refresh_rotates_tokens_and_replaying_an_old_one_revokes_the_session()
    {
        var client = NewClient();
        var first = await LoginAsync(client, ApiFactory.ViewerUser, ApiFactory.ViewerPassword);

        var refreshed = await client.PostAsJsonAsync("/api/auth/refresh", new RefreshTokenRequest { RefreshToken = first.RefreshToken });
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        var second = (await refreshed.Content.ReadFromJsonAsync<TokenResponse>(Json))!;
        Assert.NotEqual(first.RefreshToken, second.RefreshToken);

        var replay = await client.PostAsJsonAsync("/api/auth/refresh", new RefreshTokenRequest { RefreshToken = first.RefreshToken });
        var afterReplay = await client.PostAsJsonAsync("/api/auth/refresh", new RefreshTokenRequest { RefreshToken = second.RefreshToken });

        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, afterReplay.StatusCode);
    }

    [Fact]
    public async Task Revoke_ends_the_session()
    {
        var client = NewClient();
        var token = await LoginAsync(client, ApiFactory.ViewerUser, ApiFactory.ViewerPassword);

        var revoke = await client.PostAsJsonAsync("/api/auth/revoke", new RefreshTokenRequest { RefreshToken = token.RefreshToken });
        var refresh = await client.PostAsJsonAsync("/api/auth/refresh", new RefreshTokenRequest { RefreshToken = token.RefreshToken });

        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
    }

    [Fact]
    public async Task Rotating_an_agent_key_ends_its_sessions_and_revoking_it_blocks_logins()
    {
        using var admin = As(NewClient(), await LoginAsync(NewClient(), ApiFactory.AdminUser, ApiFactory.AdminPassword));
        var (server, session) = await NewAgentAsync();

        // Rotate: the old session's refresh token stops working.
        var rotated = await admin.PostAsJsonAsync("/api/agents", new CreateAgentRequest { ServerName = server });
        var newKey = (await rotated.Content.ReadFromJsonAsync<CreateAgentResponse>(Json))!.ApiKey;
        var oldRefresh = await NewClient().PostAsJsonAsync("/api/auth/refresh", new RefreshTokenRequest { RefreshToken = session.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, oldRefresh.StatusCode);

        // Revoke: the new key no longer logs in, and an unknown agent is a 404.
        var newSession = await NewClient().PostAsJsonAsync("/api/auth/agent-token", new AgentTokenRequest { ServerName = server, ApiKey = newKey });
        Assert.Equal(HttpStatusCode.OK, newSession.StatusCode);
        var newRefresh = (await newSession.Content.ReadFromJsonAsync<TokenResponse>(Json))!.RefreshToken;

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync(new Uri($"/api/agents/{server}", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync(new Uri($"/api/agents/missing-{Guid.NewGuid():N}", UriKind.Relative))).StatusCode);

        var afterRevoke = await NewClient().PostAsJsonAsync("/api/auth/agent-token", new AgentTokenRequest { ServerName = server, ApiKey = newKey });
        var refreshAfterRevoke = await NewClient().PostAsJsonAsync("/api/auth/refresh", new RefreshTokenRequest { RefreshToken = newRefresh });
        Assert.Equal(HttpStatusCode.Unauthorized, afterRevoke.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, refreshAfterRevoke.StatusCode);
    }

    [Fact]
    public async Task Invalid_requests_get_400_or_404_problems()
    {
        var (server, token) = await NewAgentAsync();
        using var agent = As(NewClient(), token);
        using var viewer = await ViewerAsync();

        var tooBig = new IngestLogBatch
        {
            Entries = Enumerable.Range(0, IngestLogBatch.MaxEntries + 1).Select(_ => new IngestLogEntry { Source = "s", Message = "m" }).ToList(),
        };
        Assert.Equal(HttpStatusCode.BadRequest, (await agent.PostAsJsonAsync("/api/ingest/logs", tooBig)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await agent.PostAsJsonAsync("/api/ingest/logs", new IngestLogBatch())).StatusCode);
        using var nullEntry = new StringContent("""{"entries":[null]}""", System.Text.Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.BadRequest, (await agent.PostAsync(new Uri("/api/ingest/logs", UriKind.Relative), nullEntry)).StatusCode);
        var tooManySamples = new IngestMetricBatch
        {
            Samples = Enumerable.Range(0, IngestMetricBatch.MaxSamples + 1).Select(_ => new IngestMetricSample()).ToList(),
        };
        Assert.Equal(HttpStatusCode.BadRequest, (await agent.PostAsJsonAsync("/api/ingest/metrics", tooManySamples)).StatusCode);

        var longSearch = new string('x', 501);
        Assert.Equal(HttpStatusCode.BadRequest, (await viewer.GetAsync(new Uri($"/api/logs?search={longSearch}", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await viewer.GetAsync(new Uri("/api/logs?page=0", UriKind.Relative))).StatusCode);

        var from = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(-2).ToString("O"));
        var serverId = await ServerIdAsync(viewer, server);
        Assert.Equal(HttpStatusCode.BadRequest, (await viewer.GetAsync(new Uri($"/api/metrics/{serverId}?from={from}", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.GetAsync(new Uri($"/api/metrics/{int.MaxValue}", UriKind.Relative))).StatusCode);
    }

    [Fact]
    public async Task The_api_refuses_to_start_outside_development_with_the_public_development_key()
    {
        await using var unsafeHost = new ApiFactory(fixture.ConnectionString, signingKey: "dev-only-signing-key-do-not-use-in-production-0123456789");

        var error = Assert.Throws<InvalidOperationException>(() => unsafeHost.CreateClient());

        Assert.Contains("development key", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("demo-server\n")]
    [InlineData("demo server")]
    public async Task The_api_refuses_to_start_with_a_seed_agent_the_api_could_not_manage(string serverName)
    {
        var settings = new Dictionary<string, string?> { ["Seed:Agents:1:ServerName"] = serverName, ["Seed:Agents:1:ApiKey"] = "lp_whatever" };
        await using var host = new ApiFactory(fixture.ConnectionString, extraSettings: settings);

        var error = Assert.Throws<InvalidOperationException>(() => host.CreateClient());

        Assert.Contains("Seed agent", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Retention:Interval", "00:00:00")]
    [InlineData("Retention:BatchSize", "50000")]
    [InlineData("RateLimiting:AuthPermitsPerMinute", "0")]
    public async Task The_api_refuses_to_start_with_invalid_settings(string key, string value)
    {
        await using var host = new ApiFactory(fixture.ConnectionString, extraSettings: new Dictionary<string, string?> { [key] = value });

        Assert.Throws<Microsoft.Extensions.Options.OptionsValidationException>(() => host.CreateClient());
    }

    [Fact]
    public async Task Malformed_messagepack_and_unsupported_content_types_are_client_errors()
    {
        var (_, token) = await NewAgentAsync();
        using var agent = As(NewClient(), token);

        using var truncated = new ByteArrayContent([0x81, 0xA7, (byte)'E', (byte)'n']); // map header, then a cut-off key
        truncated.Headers.ContentType = new MediaTypeHeaderValue("application/x-msgpack");
        var malformed = await agent.PostAsync(new Uri("/api/ingest/logs", UriKind.Relative), truncated);
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);

        using var msgpackLogin = new ByteArrayContent([0x80]);
        msgpackLogin.Headers.ContentType = new MediaTypeHeaderValue("application/x-msgpack");
        var login = await NewClient().PostAsync(new Uri("/api/auth/token", UriKind.Relative), msgpackLogin);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, login.StatusCode);
    }

    [Fact]
    public async Task Oversized_batches_are_rejected_while_reading_not_after_deserializing_everything()
    {
        var (_, token) = await NewAgentAsync();
        using var agent = As(NewClient(), token);

        // {"Entries": <array32 announcing 4,000,000 items>, followed by 4,000,000 empty maps (0x80)}.
        var header = new byte[] { 0x81, 0xA7, (byte)'E', (byte)'n', (byte)'t', (byte)'r', (byte)'i', (byte)'e', (byte)'s', 0xDD, 0x00, 0x3D, 0x09, 0x00 };
        var body = new byte[header.Length + 4_000_000];
        header.CopyTo(body, 0);
        Array.Fill(body, (byte)0x80, header.Length, 4_000_000);
        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-msgpack");

        // A repeated items key must not multiply the per-request budget (JSON and MessagePack).
        using var duplicateJson = new StringContent(
            """{"entries":[{"source":"a","message":"b"}],"entries":[{"source":"a","message":"b"}]}""", System.Text.Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.BadRequest, (await agent.PostAsync(new Uri("/api/ingest/logs", UriKind.Relative), duplicateJson)).StatusCode);
        byte[] entriesKey = [0xA7, (byte)'E', (byte)'n', (byte)'t', (byte)'r', (byte)'i', (byte)'e', (byte)'s'];
        using var duplicateMsgpack = new ByteArrayContent([0x82, .. entriesKey, 0x91, 0x80, .. entriesKey, 0x91, 0x80]);
        duplicateMsgpack.Headers.ContentType = new MediaTypeHeaderValue("application/x-msgpack");
        Assert.Equal(HttpStatusCode.BadRequest, (await agent.PostAsync(new Uri("/api/ingest/logs", UriKind.Relative), duplicateMsgpack)).StatusCode);

        // Too many keys, a null items list and camelCase MessagePack keys (which the item reader would ignore) are 400s.
        var tooManyKeys = "{" + string.Join(",", Enumerable.Range(0, 9).Select(i => $"\"k{i}\":0"))
            + ",\"entries\":[{\"source\":\"a\",\"message\":\"b\"}]}";
        using var manyKeys = new StringContent(tooManyKeys, System.Text.Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.BadRequest, (await agent.PostAsync(new Uri("/api/ingest/logs", UriKind.Relative), manyKeys)).StatusCode);
        using var nullEntries = new StringContent("""{"entries":null}""", System.Text.Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.BadRequest, (await agent.PostAsync(new Uri("/api/ingest/logs", UriKind.Relative), nullEntries)).StatusCode);
        byte[] camelKey = [0xA7, (byte)'e', (byte)'n', (byte)'t', (byte)'r', (byte)'i', (byte)'e', (byte)'s'];
        using var camelMsgpack = new ByteArrayContent([0x81, .. camelKey, 0x91, 0x80]);
        camelMsgpack.Headers.ContentType = new MediaTypeHeaderValue("application/x-msgpack");
        Assert.Equal(HttpStatusCode.BadRequest, (await agent.PostAsync(new Uri("/api/ingest/logs", UriKind.Relative), camelMsgpack)).StatusCode);

        var started = System.Diagnostics.Stopwatch.StartNew();
        var response = await agent.PostAsync(new Uri("/api/ingest/logs", UriKind.Relative), content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(2), $"Took {started.Elapsed}; the size limit should reject the array header immediately.");
    }

    [Fact]
    public async Task Agent_names_must_be_url_safe()
    {
        using var admin = As(NewClient(), await LoginAsync(NewClient(), ApiFactory.AdminUser, ApiFactory.AdminPassword));

        var invalid = await admin.PostAsJsonAsync("/api/agents", new CreateAgentRequest { ServerName = "web/01" });

        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task Auth_endpoints_are_rate_limited_per_client()
    {
        // A separate host with a tiny limit, so the shared one used by other tests is not affected.
        await using var limited = new ApiFactory(fixture.ConnectionString, authPermitsPerMinute: 3);
        using var client = limited.CreateClient();
        var request = new UserTokenRequest { UserName = "ghost", Password = "nope" };

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
        {
            statuses.Add((await client.PostAsJsonAsync("/api/auth/token", request)).StatusCode);
        }

        Assert.Equal([HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests], statuses);
    }
}
