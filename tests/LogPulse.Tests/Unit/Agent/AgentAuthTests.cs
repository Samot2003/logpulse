using System.Net;
using LogPulse.Agent.Auth;
using LogPulse.Agent.Options;
using LogPulse.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace LogPulse.Tests.Unit.Agent;

public sealed class AgentAuthTests : IDisposable
{
    private const string LoginPath = "/api/auth/agent-token";
    private const string RefreshPath = "/api/auth/refresh";
    private const string IngestPath = "/api/ingest/logs";

    private static readonly DateTimeOffset Now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan AccessLifetime = TimeSpan.FromMinutes(15);

    private readonly FakeTimeProvider _time = new(Now);
    private readonly StubHttpHandler _api;
    private readonly AgentTokenProvider _tokens;
    private int _issued;

    /// <summary>Server clock offset relative to the agent's clock.</summary>
    private TimeSpan _serverClockAhead = TimeSpan.Zero;

    private HttpStatusCode _loginStatus = HttpStatusCode.OK;
    private HttpStatusCode _refreshStatus = HttpStatusCode.OK;
    private readonly Queue<HttpStatusCode> _ingestStatuses = new();

    public AgentAuthTests()
    {
        _api = new StubHttpHandler(Respond);
        var options = Microsoft.Extensions.Options.Options.Create(new AgentOptions { ServerName = "web-01", ApiKey = "lp_key" });
        var auth = new AgentAuthClient(new StubHttpClientFactory(_api), options, _time);
        _tokens = new AgentTokenProvider(auth, _time, NullLogger<AgentTokenProvider>.Instance);
    }

    public void Dispose() => _tokens.Dispose();

    private HttpResponseMessage Respond(HttpRequestMessage request)
    {
        switch (request.RequestUri!.AbsolutePath)
        {
            case LoginPath when _loginStatus != HttpStatusCode.OK:
                return new HttpResponseMessage(_loginStatus);
            case RefreshPath when _refreshStatus != HttpStatusCode.OK:
                return new HttpResponseMessage(_refreshStatus);
            case LoginPath or RefreshPath:
                var n = Interlocked.Increment(ref _issued);
                return StubHttpHandler.Tokens($"access-{n}", $"refresh-{n}", _time.GetUtcNow() + _serverClockAhead, AccessLifetime);
            default:
                return new HttpResponseMessage(_ingestStatuses.Count > 0 ? _ingestStatuses.Dequeue() : HttpStatusCode.OK);
        }
    }

    private HttpMessageInvoker AuthenticatedClient() => new(new AuthTokenHandler(_tokens) { InnerHandler = _api });

    private static HttpRequestMessage IngestRequest() => new(HttpMethod.Post, new Uri("http://api.test" + IngestPath)) { Content = new ByteArrayContent([0x80]) };

    [Fact]
    public async Task The_first_request_logs_in_and_later_ones_reuse_the_token()
    {
        Assert.Equal("access-1", await _tokens.GetAccessTokenAsync(CancellationToken.None));
        _time.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal("access-1", await _tokens.GetAccessTokenAsync(CancellationToken.None));

        Assert.Equal(1, _api.CountTo(LoginPath));
        Assert.Equal(0, _api.CountTo(RefreshPath));
    }

    [Fact]
    public async Task Shortly_before_expiry_the_refresh_token_is_used()
    {
        await _tokens.GetAccessTokenAsync(CancellationToken.None);
        _time.Advance(AccessLifetime - TimeSpan.FromSeconds(30));

        Assert.Equal("access-2", await _tokens.GetAccessTokenAsync(CancellationToken.None));

        var refresh = Assert.Single(_api.Requests, r => r.Path == RefreshPath);
        Assert.Contains("refresh-1", System.Text.Encoding.UTF8.GetString(refresh.Body.Span), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refused_refresh_falls_back_to_logging_in_with_the_api_key()
    {
        await _tokens.GetAccessTokenAsync(CancellationToken.None);
        _time.Advance(AccessLifetime);
        _refreshStatus = HttpStatusCode.Unauthorized; // e.g. the key was rotated

        Assert.Equal("access-2", await _tokens.GetAccessTokenAsync(CancellationToken.None));
        Assert.Equal(2, _api.CountTo(LoginPath));
    }

    [Fact]
    public async Task Refused_credentials_raise_an_authentication_error()
    {
        _loginStatus = HttpStatusCode.Unauthorized;

        await Assert.ThrowsAsync<AgentAuthenticationException>(() => _tokens.GetAccessTokenAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_rate_limited_login_is_a_transient_http_error()
    {
        _loginStatus = HttpStatusCode.TooManyRequests;

        await Assert.ThrowsAsync<HttpRequestException>(() => _tokens.GetAccessTokenAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Concurrent_requests_share_a_single_renewal()
    {
        var release = new TaskCompletionSource();
        _api.BeforeResponse = () => release.Task;

        var calls = Enumerable.Range(0, 5).Select(_ => _tokens.GetAccessTokenAsync(CancellationToken.None)).ToList();
        release.SetResult();
        var tokens = await Task.WhenAll(calls);

        Assert.All(tokens, t => Assert.Equal("access-1", t));
        Assert.Equal(1, _api.CountTo(LoginPath));
    }

    [Fact]
    public async Task Expiry_follows_the_server_lifetime_even_when_the_agent_clock_is_off()
    {
        // The API's clock is an hour ahead: its absolute expiry time would already be "in the future" for an hour.
        _serverClockAhead = TimeSpan.FromHours(1);
        await _tokens.GetAccessTokenAsync(CancellationToken.None);

        _time.Advance(AccessLifetime - TimeSpan.FromSeconds(30));
        await _tokens.GetAccessTokenAsync(CancellationToken.None);

        Assert.Equal(1, _api.CountTo(RefreshPath));
    }

    [Fact]
    public async Task The_handler_adds_the_bearer_token()
    {
        using var client = AuthenticatedClient();

        using var response = await client.SendAsync(IngestRequest(), CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("access-1", _api.Requests.Single(r => r.Path == IngestPath).BearerToken);
    }

    [Fact]
    public async Task On_a_401_the_handler_renews_once_and_retries_with_the_new_token()
    {
        using var client = AuthenticatedClient();
        _ingestStatuses.Enqueue(HttpStatusCode.Unauthorized); // e.g. the API restarted with a new signing key

        using var response = await client.SendAsync(IngestRequest(), CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["access-1", "access-2"], _api.Requests.Where(r => r.Path == IngestPath).Select(r => r.BearerToken));
        Assert.Equal(1, _api.CountTo(RefreshPath));
    }

    [Fact]
    public async Task A_second_401_is_returned_instead_of_looping()
    {
        using var client = AuthenticatedClient();
        _ingestStatuses.Enqueue(HttpStatusCode.Unauthorized);
        _ingestStatuses.Enqueue(HttpStatusCode.Unauthorized);

        using var response = await client.SendAsync(IngestRequest(), CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(2, _api.CountTo(IngestPath));
    }
}
