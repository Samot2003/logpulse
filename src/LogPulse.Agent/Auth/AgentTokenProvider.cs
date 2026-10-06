namespace LogPulse.Agent.Auth;

/// <summary>
/// Keeps the agent's access token fresh: logs in with the API key, renews shortly before expiry with the refresh
/// token, and logs in again when a refresh is refused (key rotated, session too old, token reuse detected).
/// Renewals are serialized: two concurrent refreshes with the same single-use token would make the API revoke
/// the whole session. Tokens live only in memory; after a restart the agent simply logs in again.
/// </summary>
public sealed partial class AgentTokenProvider(AgentAuthClient auth, TimeProvider time, ILogger<AgentTokenProvider> logger) : IDisposable
{
    /// <summary>Renew this long before expiry (or at half the lifetime, if shorter).</summary>
    public static readonly TimeSpan RenewBefore = TimeSpan.FromMinutes(1);

    private readonly SemaphoreSlim _renewal = new(1, 1);
    private AgentSession? _session;

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _session) is { } current && IsFresh(current))
        {
            return current.AccessToken;
        }

        await _renewal.WaitAsync(cancellationToken);
        try
        {
            // Another request may have renewed it while this one was waiting.
            var session = Volatile.Read(ref _session);
            if (session is not null && IsFresh(session))
            {
                return session.AccessToken;
            }

            session = await RenewAsync(session, cancellationToken);
            Volatile.Write(ref _session, session);
            return session.AccessToken;
        }
        finally
        {
            _renewal.Release();
        }
    }

    /// <summary>The API refused this access token before its expiry: renew it on the next request.</summary>
    public void Invalidate(string accessToken)
    {
        var current = Volatile.Read(ref _session);
        if (current is not null && current.AccessToken == accessToken)
        {
            // Only if no other request has replaced the session in the meantime.
            Interlocked.CompareExchange(ref _session, current with { AccessTokenExpiresAt = DateTimeOffset.MinValue }, current);
        }
    }

    public void Dispose() => _renewal.Dispose();

    private async Task<AgentSession> RenewAsync(AgentSession? current, CancellationToken cancellationToken)
    {
        if (current is not null && time.GetUtcNow() < current.RefreshTokenExpiresAt - RenewBefore)
        {
            // A network error here propagates: the refresh token is kept and tried again next time. If the API had
            // consumed it already, that retry counts as reuse, the API answers 401 and the agent logs in again below.
            var refreshed = await auth.RefreshAsync(current.RefreshToken, cancellationToken);
            if (refreshed is not null)
            {
                return refreshed;
            }

            LogRefreshRefused(logger);
        }

        var session = await auth.LoginAsync(cancellationToken)
            ?? throw new AgentAuthenticationException("The API rejected the agent's server name or API key.");
        LogLoggedIn(logger);
        return session;
    }

    private bool IsFresh(AgentSession session)
    {
        var margin = TimeSpan.FromTicks(Math.Min(RenewBefore.Ticks, (session.AccessTokenExpiresAt - session.ObtainedAt).Ticks / 2));
        return time.GetUtcNow() < session.AccessTokenExpiresAt - margin;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Agent logged in to the API")]
    private static partial void LogLoggedIn(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The API refused the refresh token; logging in again with the API key")]
    private static partial void LogRefreshRefused(ILogger logger);
}
