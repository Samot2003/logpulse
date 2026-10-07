using System.Security.Cryptography;
using Microsoft.Extensions.Caching.Memory;

namespace LogPulse.Dashboard.Auth;

/// <summary>The dashboard session of a user: lives on the server, the browser only holds its id in a cookie.</summary>
public sealed class DashboardSession
{
    private SessionTokens _tokens;

    internal DashboardSession(string id, string userName, string? clientAddress, SessionTokens tokens)
    {
        Id = id;
        UserName = userName;
        ClientAddress = clientAddress;
        _tokens = tokens;
    }

    public string Id { get; }

    public string UserName { get; }

    /// <summary>The user's IP address at login, forwarded to the API on refresh and logout.</summary>
    public string? ClientAddress { get; }

    public SessionTokens Tokens
    {
        get => Volatile.Read(ref _tokens);
        internal set => Volatile.Write(ref _tokens, value);
    }

    /// <summary>Guards <see cref="Renewal"/> and <see cref="Ended"/>.</summary>
    internal object Gate { get; } = new();

    /// <summary>Set on logout, so a renewal that finishes afterwards cannot bring the session back.</summary>
    internal bool Ended { get; set; }

    /// <summary>The refresh in progress, shared by every caller that needs a new access token at the same time.</summary>
    internal Task<SessionTokens>? Renewal { get; set; }
}

/// <summary>The session no longer exists: logged out, expired, or the API refused its refresh token.</summary>
public sealed class SessionExpiredException : Exception
{
    public SessionExpiredException()
        : base("The dashboard session has expired. Log in again.")
    {
    }

    public SessionExpiredException(string message)
        : base(message)
    {
    }

    public SessionExpiredException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Keeps every user's tokens on the server (backend-for-frontend), so the JWT and the refresh token never reach the
/// browser. All the browser tabs and live connections of a session share one entry: refresh tokens work only once,
/// so a single place has to own the rotation. Sessions live in memory: a restart of the dashboard logs everyone out,
/// and running several instances needs a shared cache.
/// </summary>
public sealed partial class SessionStore(IMemoryCache cache, IAuthApi auth, TimeProvider time, ILogger<SessionStore> logger)
{
    /// <summary>Access tokens are renewed this long before they expire, so a request never leaves with an expired one.</summary>
    public static readonly TimeSpan RenewBefore = TimeSpan.FromMinutes(1);

    private readonly ILogger _logger = logger;

    public DashboardSession Create(string userName, string? clientAddress, SessionTokens tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        // 256 random bits: the cookie that carries it is also encrypted, but the id alone must not be guessable.
        var session = new DashboardSession(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), userName, clientAddress, tokens);
        Store(session);
        return session;
    }

    public DashboardSession? Find(string sessionId) => cache.Get<DashboardSession>(Key(sessionId));

    /// <summary>
    /// A valid access token for the session, renewed first if it is about to expire. Pass the token the API just
    /// rejected as <paramref name="rejectedToken"/> to force a renewal; concurrent callers that saw the same rejected
    /// token share one refresh.
    /// </summary>
    /// <exception cref="SessionExpiredException">The session is gone or the API refused to renew it.</exception>
    public async Task<string> GetAccessTokenAsync(string sessionId, string? rejectedToken, CancellationToken cancellationToken)
    {
        var session = Find(sessionId) ?? throw new SessionExpiredException();
        var tokens = session.Tokens;
        var mustRenew = rejectedToken is null
            ? tokens.AccessTokenExpiresAt - RenewBefore <= time.GetUtcNow()
            : rejectedToken == tokens.AccessToken;
        if (!mustRenew)
        {
            return tokens.AccessToken;
        }

        return (await RenewAsync(session, tokens).WaitAsync(cancellationToken)).AccessToken;
    }

    /// <summary>Logs out: forgets the session and revokes its refresh token in the API (best effort).</summary>
    public async Task EndAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (Find(sessionId) is not { } session)
        {
            return;
        }

        lock (session.Gate)
        {
            session.Ended = true;
        }

        cache.Remove(Key(sessionId));
        await RevokeAsync(session, session.Tokens.RefreshToken, cancellationToken);
    }

    private Task<SessionTokens> RenewAsync(DashboardSession session, SessionTokens stale)
    {
        lock (session.Gate)
        {
            if (!ReferenceEquals(session.Tokens, stale))
            {
                // Someone renewed while this caller was deciding: the new tokens are good.
                return Task.FromResult(session.Tokens);
            }

            if (session.Renewal is not { IsCompleted: false })
            {
                session.Renewal = RefreshAsync(session, stale);
            }

            return session.Renewal;
        }
    }

    private async Task<SessionTokens> RefreshAsync(DashboardSession session, SessionTokens stale)
    {
        // Not tied to any caller's cancellation: other callers may be waiting for this refresh, and abandoning it
        // after the API consumed the refresh token would lose the session. The HTTP client has its own timeout.
        var renewed = await auth.RefreshAsync(stale.RefreshToken, session.ClientAddress, CancellationToken.None);
        if (renewed is null)
        {
            cache.Remove(Key(session.Id));
            LogRefreshRefused(session.UserName);
            throw new SessionExpiredException();
        }

        bool ended;
        lock (session.Gate)
        {
            ended = session.Ended;
            if (!ended)
            {
                session.Tokens = renewed;
                Store(session);
            }
        }

        if (ended)
        {
            // Logged out while this refresh was in flight: the new tokens must not outlive the logout either.
            await RevokeAsync(session, renewed.RefreshToken, CancellationToken.None);
            throw new SessionExpiredException();
        }

        return renewed;
    }

    private async Task RevokeAsync(DashboardSession session, string refreshToken, CancellationToken cancellationToken)
    {
        try
        {
            await auth.RevokeAsync(refreshToken, session.ClientAddress, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // The session is gone from the dashboard anyway; the refresh token expires on its own.
            LogRevokeFailed(ex);
        }
    }

    // The session cannot outlive its refresh token: after that the API would refuse to renew it anyway. The cache
    // runs on the system clock, so the lifetime is given as a duration measured with this store's clock.
    private void Store(DashboardSession session)
    {
        var lifetime = session.Tokens.RefreshTokenExpiresAt - time.GetUtcNow();
        if (lifetime <= TimeSpan.Zero)
        {
            cache.Remove(Key(session.Id));
            return;
        }

        cache.Set(Key(session.Id), session, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = lifetime });
    }

    private static string Key(string sessionId) => "session:" + sessionId;

    [LoggerMessage(Level = LogLevel.Information, Message = "The API refused to renew the session of {UserName}; they have to log in again")]
    private partial void LogRefreshRefused(string userName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not revoke the refresh token of a closed session")]
    private partial void LogRevokeFailed(Exception exception);
}
