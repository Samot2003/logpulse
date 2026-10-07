using System.Net;
using System.Security.Claims;
using System.Text;
using LogPulse.Core.Models;
using LogPulse.Core.Queries;
using LogPulse.Dashboard.Api;
using LogPulse.Dashboard.Auth;
using LogPulse.Tests.Fakes;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace LogPulse.Tests.Unit.Dashboard;

/// <summary>An auth API the test controls; counts refreshes and can hold them in flight.</summary>
internal sealed class FakeAuthApi : IAuthApi
{
    private int _issued;

    public FakeAuthApi(TimeProvider time) => Time = time;

    public TimeProvider Time { get; }

    public TimeSpan AccessLifetime { get; set; } = TimeSpan.FromMinutes(15);

    public List<string> Refreshed { get; } = [];

    public List<(string Token, string? ClientAddress)> Revoked { get; } = [];

    /// <summary>When true, refresh tokens are refused (as the API does for expired, revoked or reused ones).</summary>
    public bool RefuseRefresh { get; set; }

    public Exception? RevokeFailure { get; set; }

    /// <summary>Awaited before a refresh answers, so a test can make callers overlap.</summary>
    public TaskCompletionSource? HoldRefresh { get; set; }

    public SessionTokens Issue()
    {
        var n = Interlocked.Increment(ref _issued);
        var now = Time.GetUtcNow();
        return new SessionTokens($"access-{n}", now + AccessLifetime, $"refresh-{n}", now.AddDays(7));
    }

    public Task<LoginResult> LoginAsync(string userName, string password, string? clientAddress, CancellationToken cancellationToken) =>
        Task.FromResult(password == "right" ? new LoginResult(LoginOutcome.Success, Issue()) : new LoginResult(LoginOutcome.InvalidCredentials));

    public async Task<SessionTokens?> RefreshAsync(string refreshToken, string? clientAddress, CancellationToken cancellationToken)
    {
        lock (Refreshed)
        {
            Refreshed.Add(refreshToken);
        }

        if (HoldRefresh is { } hold)
        {
            await hold.Task;
        }

        return RefuseRefresh ? null : Issue();
    }

    public Task RevokeAsync(string refreshToken, string? clientAddress, CancellationToken cancellationToken)
    {
        Revoked.Add((refreshToken, clientAddress));
        return RevokeFailure is null ? Task.CompletedTask : Task.FromException(RevokeFailure);
    }
}

internal sealed class FixedAuthenticationState(ClaimsPrincipal user) : AuthenticationStateProvider
{
    public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(user));
}

public sealed class SessionStoreTests : IDisposable
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly FakeAuthApi _auth;
    private readonly SessionStore _sessions;

    public SessionStoreTests()
    {
        _auth = new FakeAuthApi(_time);
        _sessions = new SessionStore(_cache, _auth, _time, NullLogger<SessionStore>.Instance);
    }

    public void Dispose() => _cache.Dispose();

    private DashboardSession NewSession() => _sessions.Create("ana", "203.0.113.7", _auth.Issue());

    [Fact]
    public void Session_ids_are_long_random_and_unique()
    {
        var a = NewSession();
        var b = NewSession();

        Assert.Equal(64, a.Id.Length);
        Assert.NotEqual(a.Id, b.Id);
        Assert.Same(a, _sessions.Find(a.Id));
        Assert.Null(_sessions.Find("unknown"));
    }

    [Fact]
    public async Task A_valid_token_is_returned_without_calling_the_api()
    {
        var session = NewSession();

        Assert.Equal(session.Tokens.AccessToken, await _sessions.GetAccessTokenAsync(session.Id, null, default));
        Assert.Empty(_auth.Refreshed);
    }

    [Fact]
    public async Task A_token_about_to_expire_is_renewed_first()
    {
        var session = NewSession();
        var first = session.Tokens;
        _time.Advance(_auth.AccessLifetime - SessionStore.RenewBefore);

        var token = await _sessions.GetAccessTokenAsync(session.Id, null, default);

        Assert.NotEqual(first.AccessToken, token);
        Assert.Equal([first.RefreshToken], _auth.Refreshed);
        Assert.Equal(token, session.Tokens.AccessToken);
    }

    [Fact]
    public async Task Concurrent_callers_share_one_refresh()
    {
        var session = NewSession();
        _time.Advance(_auth.AccessLifetime);
        _auth.HoldRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var calls = Enumerable.Range(0, 5).Select(_ => _sessions.GetAccessTokenAsync(session.Id, null, default)).ToList();
        _auth.HoldRefresh.SetResult();
        var tokens = await Task.WhenAll(calls);

        Assert.Single(_auth.Refreshed);
        Assert.Single(tokens.Distinct());
    }

    [Fact]
    public async Task A_rejected_token_is_renewed_once_even_if_several_requests_saw_it_rejected()
    {
        var session = NewSession();
        var rejected = session.Tokens.AccessToken;

        var renewed = await _sessions.GetAccessTokenAsync(session.Id, rejected, default);
        var again = await _sessions.GetAccessTokenAsync(session.Id, rejected, default);

        Assert.NotEqual(rejected, renewed);
        Assert.Equal(renewed, again);
        Assert.Single(_auth.Refreshed);
    }

    [Fact]
    public async Task A_refused_refresh_ends_the_session()
    {
        var session = NewSession();
        _auth.RefuseRefresh = true;

        await Assert.ThrowsAsync<SessionExpiredException>(() => _sessions.GetAccessTokenAsync(session.Id, session.Tokens.AccessToken, default));

        Assert.Null(_sessions.Find(session.Id));
        await Assert.ThrowsAsync<SessionExpiredException>(() => _sessions.GetAccessTokenAsync(session.Id, null, default));
    }

    [Fact]
    public async Task A_failed_refresh_is_retried_by_the_next_caller()
    {
        var session = NewSession();
        var stale = session.Tokens.AccessToken;
        var failing = new FailingOnceAuthApi(_auth);
        var sessions = new SessionStore(_cache, failing, _time, NullLogger<SessionStore>.Instance);

        await Assert.ThrowsAsync<HttpRequestException>(() => sessions.GetAccessTokenAsync(session.Id, stale, default));

        Assert.NotEqual(stale, await sessions.GetAccessTokenAsync(session.Id, stale, default));
        Assert.NotNull(sessions.Find(session.Id));
    }

    [Fact]
    public async Task Ending_a_session_forgets_it_and_revokes_its_refresh_token_from_the_users_address()
    {
        var session = NewSession();

        await _sessions.EndAsync(session.Id, default);

        Assert.Null(_sessions.Find(session.Id));
        Assert.Equal([(session.Tokens.RefreshToken, "203.0.113.7")], _auth.Revoked);
    }

    [Fact]
    public async Task A_logout_during_a_renewal_wins_and_the_renewed_tokens_are_revoked_too()
    {
        var session = NewSession();
        _auth.HoldRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var renewal = _sessions.GetAccessTokenAsync(session.Id, session.Tokens.AccessToken, default);

        await _sessions.EndAsync(session.Id, default);
        _auth.HoldRefresh.SetResult();

        await Assert.ThrowsAsync<SessionExpiredException>(() => renewal);
        Assert.Null(_sessions.Find(session.Id));
        Assert.Equal(["refresh-1", "refresh-2"], _auth.Revoked.Select(r => r.Token));
    }

    [Fact]
    public async Task Logout_works_even_if_the_api_cannot_revoke()
    {
        var session = NewSession();
        _auth.RevokeFailure = new HttpRequestException("API down");

        await _sessions.EndAsync(session.Id, default);
        await _sessions.EndAsync("unknown", default);

        Assert.Null(_sessions.Find(session.Id));
    }

    private sealed class FailingOnceAuthApi(FakeAuthApi inner) : IAuthApi
    {
        private bool _failed;

        public Task<LoginResult> LoginAsync(string userName, string password, string? clientAddress, CancellationToken cancellationToken) =>
            inner.LoginAsync(userName, password, clientAddress, cancellationToken);

        public Task<SessionTokens?> RefreshAsync(string refreshToken, string? clientAddress, CancellationToken cancellationToken)
        {
            if (_failed)
            {
                return inner.RefreshAsync(refreshToken, clientAddress, cancellationToken);
            }

            _failed = true;
            return Task.FromException<SessionTokens?>(new HttpRequestException("timeout"));
        }

        public Task RevokeAsync(string refreshToken, string? clientAddress, CancellationToken cancellationToken) =>
            inner.RevokeAsync(refreshToken, clientAddress, cancellationToken);
    }
}

public class DashboardHttpClientTests
{
    private static readonly DateTimeOffset ApiNow = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Login_maps_the_api_answers_and_forwards_the_users_address()
    {
        var status = HttpStatusCode.OK;
        var handler = new StubHttpHandler(_ => status == HttpStatusCode.OK
            ? StubHttpHandler.Tokens("a1", "r1", ApiNow, TimeSpan.FromMinutes(15))
            : new HttpResponseMessage(status));
        var time = new FakeTimeProvider(ApiNow.AddMinutes(10)); // the dashboard's clock runs 10 minutes ahead
        var auth = new AuthApiClient(new StubHttpClientFactory(handler), time);

        var ok = await auth.LoginAsync("ana", "secret", "203.0.113.7", default);
        status = HttpStatusCode.Unauthorized;
        var wrong = await auth.LoginAsync("ana", "nope", null, default);
        status = HttpStatusCode.TooManyRequests;
        var limited = await auth.LoginAsync("ana", "nope", null, default);

        Assert.Equal(LoginOutcome.Success, ok.Outcome);
        Assert.Equal(time.GetUtcNow().AddMinutes(15), ok.Tokens!.AccessTokenExpiresAt); // anchored to the local clock
        Assert.Equal((LoginOutcome.InvalidCredentials, LoginOutcome.TooManyAttempts), (wrong.Outcome, limited.Outcome));
        Assert.Equal("/api/auth/token", handler.Requests[0].Path);
        Assert.Equal("203.0.113.7", handler.Requests[0].ForwardedFor);
        Assert.Null(handler.Requests[1].ForwardedFor);
        Assert.Contains("\"userName\":\"ana\"", Encoding.UTF8.GetString(handler.Requests[0].Body.Span), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refused_refresh_is_null_and_an_unavailable_api_is_an_error()
    {
        var status = HttpStatusCode.Unauthorized;
        var auth = new AuthApiClient(new StubHttpClientFactory(new StubHttpHandler(_ => new HttpResponseMessage(status))), TimeProvider.System);

        Assert.Null(await auth.RefreshAsync("r1", null, default));
        status = HttpStatusCode.TooManyRequests;
        await Assert.ThrowsAsync<HttpRequestException>(() => auth.RefreshAsync("r1", null, default));
    }

    [Fact]
    public void Log_queries_send_only_the_filters_that_are_set()
    {
        Assert.Equal("api/logs?page=1&pageSize=50", LogPulseApiClient.LogsPath(new LogQuery()));

        var path = LogPulseApiClient.LogsPath(new LogQuery
        {
            ServerId = 3,
            MinSeverity = LogSeverity.Warning,
            Search = "disk full & 100%",
            From = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero),
            Page = 2,
            PageSize = 25,
        });

        Assert.Equal("api/logs?page=2&pageSize=25&serverId=3&minSeverity=Warning&search=disk%20full%20%26%20100%25&from=2026-01-01T10%3A00%3A00.0000000%2B00%3A00", path);
    }

    [Fact]
    public async Task Queries_carry_the_users_token_and_a_rejected_token_is_renewed_once()
    {
        var time = new FakeTimeProvider(ApiNow);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var auth = new FakeAuthApi(time);
        var sessions = new SessionStore(cache, auth, time, NullLogger<SessionStore>.Instance);
        var session = sessions.Create("ana", null, auth.Issue());
        var firstToken = session.Tokens.AccessToken;
        var handler = new StubHttpHandler(request => request.Headers.Authorization?.Parameter == firstToken
            ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
            : StubHttpHandler.Json(new[] { new { Id = 1, Name = "web-01", RegisteredAt = ApiNow, LastSeenAt = ApiNow } }));
        var api = Client(handler, sessions, session);

        var servers = await api.GetServersAsync();

        Assert.Equal("web-01", Assert.Single(servers).Name);
        Assert.Equal([firstToken, session.Tokens.AccessToken], handler.Requests.Select(r => r.BearerToken));
        Assert.Single(auth.Refreshed);
    }

    [Fact]
    public async Task A_renewed_token_refused_again_ends_the_session()
    {
        var time = new FakeTimeProvider(ApiNow);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var sessions = new SessionStore(cache, new FakeAuthApi(time), time, NullLogger<SessionStore>.Instance);
        var session = sessions.Create("ana", null, new FakeAuthApi(time).Issue());
        var api = Client(new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)), sessions, session);

        await Assert.ThrowsAsync<SessionExpiredException>(() => api.GetServersAsync());

        Assert.Null(sessions.Find(session.Id));
    }

    [Fact]
    public async Task Enums_arrive_as_strings_and_api_errors_are_exceptions()
    {
        var time = new FakeTimeProvider(ApiNow);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var auth = new FakeAuthApi(time);
        var sessions = new SessionStore(cache, auth, time, NullLogger<SessionStore>.Instance);
        var session = sessions.Create("ana", null, auth.Issue());
        var fail = false;
        var handler = new StubHttpHandler(_ => fail
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"items":[{"id":5,"serverId":1,"timestamp":"2026-01-01T12:00:00+00:00","severity":"Critical","source":"app","message":"down"}],"page":1,"pageSize":50,"totalCount":1}""",
                    Encoding.UTF8,
                    "application/json"),
            });
        var api = Client(handler, sessions, session);

        var page = await api.GetLogsAsync(new LogQuery());
        fail = true;
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => api.GetLatestMetricsAsync());

        Assert.Equal(LogSeverity.Critical, Assert.Single(page.Items).Severity);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, error.StatusCode);
    }

    [Fact]
    public async Task Without_a_session_nothing_is_sent()
    {
        var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var sessions = new SessionStore(cache, new FakeAuthApi(TimeProvider.System), TimeProvider.System, NullLogger<SessionStore>.Instance);
        var api = new LogPulseApiClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://api.test/") },
            new CurrentSession(new FixedAuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()))),
            sessions);

        await Assert.ThrowsAsync<SessionExpiredException>(() => api.GetServersAsync());

        Assert.Empty(handler.Requests);
    }

    private static LogPulseApiClient Client(StubHttpHandler handler, SessionStore sessions, DashboardSession session) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://api.test/") }, new CurrentSession(new FixedAuthenticationState(DashboardClaims.Principal(session))), sessions);
}
