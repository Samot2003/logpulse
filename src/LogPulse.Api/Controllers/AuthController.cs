using LogPulse.Api.Auth;
using LogPulse.Api.Infrastructure;
using LogPulse.Core.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace LogPulse.Api.Controllers;

[ApiController]
[Route("api/auth")]
[AllowAnonymous]
[EnableRateLimiting(Policies.AuthRateLimit)]
[Produces("application/json")]
// Credentials and tokens are a few hundred bytes; a small cap stops anonymous callers from making the API buffer large bodies.
[RequestSizeLimit(16 * 1024)]
public sealed class AuthController(AuthService auth, TokenService tokens) : ControllerBase
{
    /// <summary>Dashboard login with user name and password.</summary>
    [HttpPost("token")]
    [ProducesResponseType<TokenResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> UserToken(UserTokenRequest request, CancellationToken cancellationToken) =>
        Respond(await auth.LoginUserAsync(request, cancellationToken));

    /// <summary>Agent login with its server name and API key.</summary>
    [HttpPost("agent-token")]
    [ProducesResponseType<TokenResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> AgentToken(AgentTokenRequest request, CancellationToken cancellationToken) =>
        Respond(await auth.AuthenticateAgentAsync(request, cancellationToken));

    /// <summary>Exchanges a refresh token for a new pair. Each refresh token works only once.</summary>
    [HttpPost("refresh")]
    [ProducesResponseType<TokenResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Refresh(RefreshTokenRequest request, CancellationToken cancellationToken) =>
        Respond(await tokens.RefreshAsync(request.RefreshToken, cancellationToken));

    /// <summary>Logs out: revokes the refresh token and every token rotated from it.</summary>
    [HttpPost("revoke")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Revoke(RefreshTokenRequest request, CancellationToken cancellationToken)
    {
        await tokens.RevokeAsync(request.RefreshToken, cancellationToken);
        return NoContent();
    }

    // Same answer for every failure (unknown user, wrong password, revoked key, reused token).
    private IActionResult Respond(TokenResponse? response) =>
        response is null
            ? Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Invalid credentials")
            : Ok(response);
}
