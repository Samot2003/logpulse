using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LogPulse.Agent.Options;
using LogPulse.Core.Contracts;
using Microsoft.Extensions.Options;

namespace LogPulse.Agent.Auth;

/// <summary>The agent's current session, with expiry times converted to the agent's own clock.</summary>
public sealed record AgentSession(
    string AccessToken,
    DateTimeOffset ObtainedAt,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt);

/// <summary>The API refused the agent's server name or API key (revoked, rotated or mistyped).</summary>
public sealed class AgentAuthenticationException : Exception
{
    public AgentAuthenticationException()
    {
    }

    public AgentAuthenticationException(string message)
        : base(message)
    {
    }

    public AgentAuthenticationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Calls the API's login and refresh endpoints. Deliberately without automatic retries: a refresh token works only
/// once, and retrying one whose response was lost would look like a stolen token to the API, which then revokes
/// the session.
/// </summary>
public sealed class AgentAuthClient(IHttpClientFactory httpClients, IOptions<AgentOptions> options, TimeProvider time)
{
    public const string HttpClientName = "LogPulse.Auth";

    private static readonly Uri LoginEndpoint = new("api/auth/agent-token", UriKind.Relative);
    private static readonly Uri RefreshEndpoint = new("api/auth/refresh", UriKind.Relative);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Logs in with the API key. Returns null if the API refuses the credentials.</summary>
    public Task<AgentSession?> LoginAsync(CancellationToken cancellationToken) =>
        PostAsync(LoginEndpoint, new AgentTokenRequest { ServerName = options.Value.ServerName, ApiKey = options.Value.ApiKey }, cancellationToken);

    /// <summary>Exchanges the refresh token for a new session. Returns null if the API refuses it.</summary>
    public Task<AgentSession?> RefreshAsync(string refreshToken, CancellationToken cancellationToken) =>
        PostAsync(RefreshEndpoint, new RefreshTokenRequest { RefreshToken = refreshToken }, cancellationToken);

    private async Task<AgentSession?> PostAsync<TRequest>(Uri endpoint, TRequest body, CancellationToken cancellationToken)
    {
        using var response = await httpClients.CreateClient(HttpClientName).PostAsJsonAsync(endpoint, body, Json, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            return null;
        }

        // 429 (rate limited) or 5xx: an exception, and the sender tries again later.
        response.EnsureSuccessStatusCode();
        var tokens = await response.Content.ReadFromJsonAsync<TokenResponse>(Json, cancellationToken)
            ?? throw new HttpRequestException("The API returned an empty token response.");

        // Expiry times come from the API's clock. Measuring them against the response's Date header and anchoring
        // them to the local clock means a skewed agent clock neither renews on every request nor uses expired tokens.
        var localNow = time.GetUtcNow();
        var serverNow = response.Headers.Date ?? localNow;
        return new AgentSession(
            tokens.AccessToken,
            localNow,
            localNow + (tokens.AccessTokenExpiresAt - serverNow),
            tokens.RefreshToken,
            localNow + (tokens.RefreshTokenExpiresAt - serverNow));
    }
}
