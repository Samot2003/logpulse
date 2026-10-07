using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using LogPulse.Core.Contracts;
using LogPulse.Core.Models;
using LogPulse.Dashboard.Auth;
using LogPulse.Dashboard.Live;
using LogPulse.Dashboard.Options;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LogPulse.Tests.Integration;

/// <summary>Hosts the real dashboard in memory, talking to the in-memory API (and SQL Server) for every call.</summary>
public sealed class DashboardFactory(ApiFactory api) : WebApplicationFactory<DashboardOptions>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Not "Development": secure cookies, HSTS and the error page, like production.
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Dashboard:ApiBaseUrl"] = api.Server.BaseAddress.ToString(),
        }));
        builder.ConfigureTestServices(services =>
        {
            services.ConfigureHttpClientDefaults(http => http.ConfigurePrimaryHttpMessageHandler(() => api.Server.CreateHandler()));
            services.Configure<LiveHubConnectionOptions>(live => live.ConfigureHttp = http =>
            {
                http.HttpMessageHandlerFactory = _ => api.Server.CreateHandler();
                http.Transports = HttpTransportType.LongPolling;
            });
        });
    }
}

[Collection(SqlServerGroup.Name)]
[Trait("Category", "Integration")]
public sealed partial class DashboardEndToEndTests(SqlServerFixture fixture) : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);

    private readonly DashboardFactory _dashboard = new(fixture.Api);

    public ValueTask DisposeAsync() => _dashboard.DisposeAsync();

    private HttpClient Browser() => _dashboard.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        BaseAddress = new Uri("https://localhost/"),
        HandleCookies = true,
    });

    [GeneratedRegex("<input[^>]*name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"")]
    private static partial Regex AntiforgeryField();

    private static async Task<string> AntiforgeryTokenAsync(HttpClient browser, string page)
    {
        var html = await browser.GetStringAsync(page);
        return AntiforgeryField().Match(html) is { Success: true } match
            ? WebUtility.HtmlDecode(match.Groups[1].Value)
            : throw new InvalidOperationException($"No antiforgery token on {page}.");
    }

    private static async Task<HttpResponseMessage> LogInAsync(HttpClient browser, string userName, string password, string? returnUrl = null)
    {
        var page = returnUrl is null ? "login" : "login?returnUrl=" + Uri.EscapeDataString(returnUrl);
        var token = await AntiforgeryTokenAsync(browser, page);
        return await browser.PostAsync(page, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_handler"] = "login",
            ["__RequestVerificationToken"] = token,
            ["Input.UserName"] = userName,
            ["Input.Password"] = password,
        }));
    }

    private async Task<(int ServerId, IngestResult Result)> IngestAsync(string message)
    {
        using var client = fixture.Api.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/agent-token", new AgentTokenRequest { ServerName = ApiFactory.SeededAgentServer, ApiKey = ApiFactory.SeededAgentKey });
        var token = (await login.Content.ReadFromJsonAsync<TokenResponse>(Json))!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        var batch = new IngestLogBatch { Entries = [new IngestLogEntry { Timestamp = DateTimeOffset.UtcNow, Severity = LogSeverity.Error, Source = "e2e", Message = message }] };
        var response = await client.PostAsJsonAsync("/api/ingest/logs", batch, Json);
        response.EnsureSuccessStatusCode();
        var result = (await response.Content.ReadFromJsonAsync<IngestResult>(Json))!;

        using var viewer = fixture.Api.CreateClient();
        var viewerLogin = await viewer.PostAsJsonAsync("/api/auth/token", new UserTokenRequest { UserName = ApiFactory.ViewerUser, Password = ApiFactory.ViewerPassword });
        viewer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await viewerLogin.Content.ReadFromJsonAsync<TokenResponse>(Json))!.AccessToken);
        var servers = await viewer.GetFromJsonAsync<List<Server>>("/api/servers", Json);
        return (servers!.Single(s => s.Name == ApiFactory.SeededAgentServer).Id, result);
    }

    [Fact]
    public async Task Pages_send_anonymous_users_to_the_login_page()
    {
        using var browser = Browser();

        var response = await browser.GetAsync("logs");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/login?ReturnUrl=%2Flogs", response.Headers.Location!.PathAndQuery);
    }

    [Fact]
    public async Task Logging_in_sets_a_hardened_session_cookie_and_returns_to_the_requested_page()
    {
        using var browser = Browser();

        var response = await LogInAsync(browser, ApiFactory.ViewerUser, ApiFactory.ViewerPassword, returnUrl: "/logs?minSeverity=Error");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("https://localhost/logs?minSeverity=Error", response.Headers.Location!.AbsoluteUri);
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith("LogPulse.Session=", StringComparison.Ordinal));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("expires", cookie, StringComparison.OrdinalIgnoreCase); // gone when the browser closes
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("logs")).StatusCode);
    }

    [Fact]
    public async Task A_wrong_password_is_reported_and_no_session_is_created()
    {
        using var browser = Browser();

        var response = await LogInAsync(browser, ApiFactory.ViewerUser, "wrong password");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Wrong user name or password.", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.False(response.Headers.TryGetValues("Set-Cookie", out var cookies) && cookies.Any(c => c.StartsWith("LogPulse.Session=", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_return_url_to_another_site_is_ignored()
    {
        using var browser = Browser();

        var response = await LogInAsync(browser, ApiFactory.ViewerUser, ApiFactory.ViewerPassword, returnUrl: "https://evil.example/");

        Assert.Equal("https://localhost/", response.Headers.Location!.AbsoluteUri);
    }

    [Fact]
    public async Task The_pages_are_rendered_with_the_users_data_from_the_api()
    {
        var (serverId, _) = await IngestAsync("e2e disk failure");
        using var browser = Browser();
        await LogInAsync(browser, ApiFactory.ViewerUser, ApiFactory.ViewerPassword);

        var overview = await browser.GetStringAsync("");
        var logs = await browser.GetStringAsync($"logs?serverId={serverId}&minSeverity=Error");
        var detail = await browser.GetStringAsync($"servers/{serverId}");

        Assert.Contains(ApiFactory.SeededAgentServer, overview, StringComparison.Ordinal);
        Assert.Contains("e2e disk failure", logs, StringComparison.Ordinal);
        Assert.Contains("e2e disk failure", detail, StringComparison.Ordinal);
        Assert.Contains(ApiFactory.ViewerUser, overview, StringComparison.Ordinal); // the signed-in user in the header
    }

    [Fact]
    public async Task Logging_out_ends_the_session_even_for_a_copy_of_the_cookie()
    {
        using var browser = Browser();
        var login = await LogInAsync(browser, ApiFactory.ViewerUser, ApiFactory.ViewerPassword);
        var sessionCookie = login.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("LogPulse.Session=", StringComparison.Ordinal)).Split(';')[0];
        var token = await AntiforgeryTokenAsync(browser, "logs");

        var logout = await browser.PostAsync(AccountEndpoints.LogoutPath.TrimStart('/'), new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }));

        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        Assert.Equal("/login", logout.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.Redirect, (await browser.GetAsync("logs")).StatusCode);

        // A stolen copy of the old cookie is useless: its server-side session is gone.
        using var thief = _dashboard.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost/"), HandleCookies = false });
        using var replay = new HttpRequestMessage(HttpMethod.Get, "logs");
        replay.Headers.Add("Cookie", sessionCookie);
        Assert.Equal(HttpStatusCode.Redirect, (await thief.SendAsync(replay)).StatusCode);
    }

    [Fact]
    public async Task Logout_needs_the_antiforgery_token()
    {
        using var browser = Browser();
        await LogInAsync(browser, ApiFactory.ViewerUser, ApiFactory.ViewerPassword);

        var logout = await browser.PostAsync(AccountEndpoints.LogoutPath.TrimStart('/'), new FormUrlEncodedContent(new Dictionary<string, string>()));

        Assert.Equal(HttpStatusCode.BadRequest, logout.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("logs")).StatusCode);
    }

    [Fact]
    public async Task Responses_carry_security_headers_and_health_is_public()
    {
        using var browser = Browser();

        var login = await browser.GetAsync("login");
        var health = await browser.GetAsync("health");

        var csp = Assert.Single(login.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("script-src 'self'", csp, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none'", csp, StringComparison.Ordinal);
        Assert.Equal("nosniff", Assert.Single(login.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    [Fact]
    public async Task The_live_feed_of_an_ended_session_stops_instead_of_retrying()
    {
        var login = await _dashboard.Services.GetRequiredService<IAuthApi>().LoginAsync(ApiFactory.ViewerUser, ApiFactory.ViewerPassword, null, default);
        var sessions = _dashboard.Services.GetRequiredService<SessionStore>();
        var session = sessions.Create(ApiFactory.ViewerUser, null, login.Tokens!);
        await sessions.EndAsync(session.Id, default);

        await using var circuit = _dashboard.Services.CreateAsyncScope();
        var state = (IHostEnvironmentAuthenticationStateProvider)circuit.ServiceProvider.GetRequiredService<AuthenticationStateProvider>();
        state.SetAuthenticationState(Task.FromResult(new AuthenticationState(DashboardClaims.Principal(session))));
        var feed = circuit.ServiceProvider.GetRequiredService<ILiveFeed>();
        feed.Start();

        var started = DateTime.UtcNow;
        while (feed.Status != LiveStatus.SessionExpired)
        {
            Assert.True(DateTime.UtcNow - started < Deadline, $"The live feed did not give up (status {feed.Status}).");
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task An_open_dashboard_receives_live_data_and_makes_agents_sample_at_full_speed()
    {
        var auth = _dashboard.Services.GetRequiredService<IAuthApi>();
        var login = await auth.LoginAsync(ApiFactory.ViewerUser, ApiFactory.ViewerPassword, null, default);
        var session = _dashboard.Services.GetRequiredService<SessionStore>().Create(ApiFactory.ViewerUser, null, login.Tokens!);
        var received = new TaskCompletionSource<LogsReceivedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using (var circuit = _dashboard.Services.CreateAsyncScope())
        {
            // What Blazor does when a circuit starts: the circuit's user is the one from the cookie.
            var state = (IHostEnvironmentAuthenticationStateProvider)circuit.ServiceProvider.GetRequiredService<AuthenticationStateProvider>();
            state.SetAuthenticationState(Task.FromResult(new AuthenticationState(DashboardClaims.Principal(session))));
            var feed = circuit.ServiceProvider.GetRequiredService<ILiveFeed>();
            feed.LogsReceived += e => received.TrySetResult(e);
            feed.Start();

            var started = DateTime.UtcNow;
            while (feed.Status != LiveStatus.Live)
            {
                Assert.True(DateTime.UtcNow - started < Deadline, $"The live feed did not connect (status {feed.Status}).");
                await Task.Delay(50);
            }

            var (_, result) = await IngestAsync("e2e live line");
            Assert.True(result.ViewersOnline);
            var live = await received.Task.WaitAsync(Deadline);
            Assert.Equal((ApiFactory.SeededAgentServer, LogSeverity.Error), (live.ServerName, live.MaxSeverity));
        }

        // Closing the circuit closes its connection: agents go back to the idle rate.
        var closed = DateTime.UtcNow;
        while ((await IngestAsync("e2e after close")).Result.ViewersOnline)
        {
            Assert.True(DateTime.UtcNow - closed < Deadline, "The API still counts the closed dashboard as a viewer.");
            await Task.Delay(100);
        }
    }
}
