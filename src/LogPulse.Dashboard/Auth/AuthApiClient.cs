using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LogPulse.Core.Contracts;

namespace LogPulse.Dashboard.Auth;

/// <summary>A user's tokens, with expiry times converted to the dashboard's own clock.</summary>
public sealed record SessionTokens(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt);

public enum LoginOutcome
{
    Success,
    InvalidCredentials,
    TooManyAttempts,
}

public sealed record LoginResult(LoginOutcome Outcome, SessionTokens? Tokens = null);

/// <summary>The API's auth endpoints, called on behalf of a dashboard user.</summary>
public interface IAuthApi
{
    Task<LoginResult> LoginAsync(string userName, string password, string? clientAddress, CancellationToken cancellationToken);

    /// <summary>Returns null if the API refuses the refresh token (expired, revoked or already used).</summary>
    Task<SessionTokens?> RefreshAsync(string refreshToken, string? clientAddress, CancellationToken cancellationToken);

    Task RevokeAsync(string refreshToken, string? clientAddress, CancellationToken cancellationToken);
}

/// <summary>
/// Calls /api/auth without automatic retries: a refresh token works only once, and retrying one whose response was
/// lost would look like a stolen token to the API, which then revokes the session.
/// The user's IP address travels in X-Forwarded-For, so the API rate-limits logins per user and not per dashboard;
/// the API only trusts that header from the proxies it is configured to trust.
/// </summary>
public sealed class AuthApiClient(IHttpClientFactory httpClients, TimeProvider time) : IAuthApi
{
    public const string HttpClientName = "LogPulse.Auth";

    private static readonly Uri LoginEndpoint = new("api/auth/token", UriKind.Relative);
    private static readonly Uri RefreshEndpoint = new("api/auth/refresh", UriKind.Relative);
    private static readonly Uri RevokeEndpoint = new("api/auth/revoke", UriKind.Relative);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<LoginResult> LoginAsync(string userName, string password, string? clientAddress, CancellationToken cancellationToken)
    {
        using var response = await PostAsync(LoginEndpoint, new UserTokenRequest { UserName = userName, Password = password }, clientAddress, cancellationToken);
        return response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => new LoginResult(LoginOutcome.InvalidCredentials),
            HttpStatusCode.TooManyRequests => new LoginResult(LoginOutcome.TooManyAttempts),
            _ => new LoginResult(LoginOutcome.Success, await ReadTokensAsync(response, cancellationToken)),
        };
    }

    public async Task<SessionTokens?> RefreshAsync(string refreshToken, string? clientAddress, CancellationToken cancellationToken)
    {
        using var response = await PostAsync(RefreshEndpoint, new RefreshTokenRequest { RefreshToken = refreshToken }, clientAddress, cancellationToken);
        return response.StatusCode == HttpStatusCode.Unauthorized ? null : await ReadTokensAsync(response, cancellationToken);
    }

    public async Task RevokeAsync(string refreshToken, string? clientAddress, CancellationToken cancellationToken)
    {
        using var response = await PostAsync(RevokeEndpoint, new RefreshTokenRequest { RefreshToken = refreshToken }, clientAddress, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task<HttpResponseMessage> PostAsync<TBody>(Uri endpoint, TBody body, string? clientAddress, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = JsonContent.Create(body, options: Json) };
        if (clientAddress is not null)
        {
            request.Headers.Add("X-Forwarded-For", clientAddress);
        }

        return await httpClients.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
    }

    private async Task<SessionTokens> ReadTokensAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        // 429 on refresh or 5xx: an exception, so the page shows that the API is unavailable and the session survives.
        response.EnsureSuccessStatusCode();
        var tokens = await response.Content.ReadFromJsonAsync<TokenResponse>(Json, cancellationToken)
            ?? throw new HttpRequestException("The API returned an empty token response.");

        // Expiry times come from the API's clock. Measuring them against the response's Date header and anchoring
        // them to the local clock means a skewed clock neither renews on every request nor uses expired tokens.
        var localNow = time.GetUtcNow();
        var serverNow = response.Headers.Date ?? localNow;
        return new SessionTokens(
            tokens.AccessToken,
            localNow + (tokens.AccessTokenExpiresAt - serverNow),
            tokens.RefreshToken,
            localNow + (tokens.RefreshTokenExpiresAt - serverNow));
    }
}
