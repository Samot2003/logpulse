using LogPulse.Api.Options;
using LogPulse.Core.Contracts;
using LogPulse.Core.Models;
using LogPulse.Data.Daos;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace LogPulse.Api.Auth;

/// <summary>Who a token pair is issued to.</summary>
/// <param name="Version">
/// The credential version the session is bound to: an agent's <see cref="AgentCredential.KeyVersion"/>, 0 for users.
/// </param>
public sealed record TokenSubject(TokenSubjectType Type, int Id, string Name, string Role, int Version);

/// <summary>
/// Issues short-lived access tokens (JWT) and single-use, rotating refresh tokens. Presenting a refresh token
/// that was already used revokes its whole family, so a stolen token stops working as soon as either the thief
/// or the legitimate client uses it a second time.
/// </summary>
public sealed partial class TokenService(
    IRefreshTokenDao refreshTokens,
    IUserDao users,
    IAgentCredentialDao agentCredentials,
    IOptions<JwtOptions> options,
    TimeProvider time,
    ILogger<TokenService> logger)
{
    private readonly JsonWebTokenHandler _handler = new();

    /// <summary>Starts a new session (a new token family) for the subject.</summary>
    public async Task<TokenResponse> IssueAsync(TokenSubject subject, CancellationToken cancellationToken = default)
    {
        var now = time.GetUtcNow();
        var (response, token) = CreatePair(subject, Guid.NewGuid(), familyCreatedAt: now, now);
        await refreshTokens.InsertAsync(token, cancellationToken);
        return response;
    }

    /// <summary>Exchanges a valid refresh token for a new pair. Returns null if the token cannot be used.</summary>
    public async Task<TokenResponse?> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        var stored = await refreshTokens.GetByHashAsync(Secrets.Hash(refreshToken), cancellationToken);
        if (stored is null || stored.RevokedAt is not null)
        {
            return null;
        }

        var now = time.GetUtcNow();
        if (stored.UsedAt is not null)
        {
            await RevokeFamilyAfterReuseAsync(stored, now);
            return null;
        }

        if (stored.ExpiresAt <= now || now >= SessionEnd(stored.FamilyCreatedAt))
        {
            return null;
        }

        // Two concurrent refreshes with the same token: only one wins the compare-and-set; the other is reuse.
        if (!await refreshTokens.TryMarkUsedAsync(stored.Id, now, cancellationToken))
        {
            // Losing to a revocation (logout, key rotation) is not reuse: do not raise a false security alert.
            var current = await refreshTokens.GetByHashAsync(stored.TokenHash, CancellationToken.None);
            if (current?.RevokedAt is null)
            {
                await RevokeFamilyAfterReuseAsync(stored, now);
            }

            return null;
        }

        // The old token is consumed now. Finish without honoring client cancellation: stopping here would leave
        // the session without a successor, and the client's retry would then look like token reuse.
        var subject = await LoadSubjectAsync(stored, CancellationToken.None);
        if (subject is null)
        {
            // The user was deleted or the agent key was revoked since the token was issued.
            await refreshTokens.RevokeFamilyAsync(stored.FamilyId, now, CancellationToken.None);
            return null;
        }

        var (response, rotated) = CreatePair(subject, stored.FamilyId, stored.FamilyCreatedAt, now);

        // Fails if the family was revoked while we were working (reuse detected, logout, key rotation).
        return await refreshTokens.TryInsertRotatedAsync(rotated, CancellationToken.None) ? response : null;
    }

    /// <summary>Logs out: revokes the token's whole family. Unknown tokens are ignored.</summary>
    public async Task RevokeAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        var stored = await refreshTokens.GetByHashAsync(Secrets.Hash(refreshToken), cancellationToken);
        if (stored is not null)
        {
            await refreshTokens.RevokeFamilyAsync(stored.FamilyId, time.GetUtcNow(), cancellationToken);
        }
    }

    // A session can be refreshed for at most MaxSessionDays after login; then the user or agent must log in again.
    private DateTimeOffset SessionEnd(DateTimeOffset familyCreatedAt) => familyCreatedAt.AddDays(options.Value.MaxSessionDays);

    private (TokenResponse Response, RefreshToken Stored) CreatePair(
        TokenSubject subject, Guid familyId, DateTimeOffset familyCreatedAt, DateTimeOffset now)
    {
        var settings = options.Value;
        var accessExpires = now.AddMinutes(settings.AccessTokenMinutes);
        var sessionEnd = SessionEnd(familyCreatedAt);
        var refreshExpires = now.AddDays(settings.RefreshTokenDays) < sessionEnd ? now.AddDays(settings.RefreshTokenDays) : sessionEnd;

        var accessToken = _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = settings.Issuer,
            Audience = settings.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = accessExpires.UtcDateTime,
            SigningCredentials = new SigningCredentials(JwtSetup.SigningKey(settings), SecurityAlgorithms.HmacSha256),
            Claims = new Dictionary<string, object>
            {
                [JwtSetup.SubjectClaim] = $"{subject.Type.ToString().ToLowerInvariant()}:{subject.Id}",
                [JwtSetup.NameClaim] = subject.Name,
                [JwtSetup.RoleClaim] = subject.Role,
                [JwtSetup.TokenIdClaim] = Guid.NewGuid().ToString("N"),
            },
        });

        var refreshToken = Secrets.NewRandomToken();
        var stored = new RefreshToken
        {
            TokenHash = Secrets.Hash(refreshToken),
            FamilyId = familyId,
            SubjectType = subject.Type,
            SubjectId = subject.Id,
            SubjectVersion = subject.Version,
            FamilyCreatedAt = familyCreatedAt,
            CreatedAt = now,
            ExpiresAt = refreshExpires,
        };

        return (new TokenResponse(accessToken, accessExpires, refreshToken, refreshExpires), stored);
    }

    private async Task<TokenSubject?> LoadSubjectAsync(RefreshToken token, CancellationToken cancellationToken)
    {
        // Claims are rebuilt from the database, so a role change or a revoked key applies on the next refresh.
        switch (token.SubjectType)
        {
            case TokenSubjectType.User:
                var user = await users.GetByIdAsync(token.SubjectId, cancellationToken);
                return user is null ? null : new TokenSubject(TokenSubjectType.User, user.Id, user.UserName, user.Role, Version: 0);

            case TokenSubjectType.Agent:
                var agent = await agentCredentials.GetByIdAsync(token.SubjectId, cancellationToken);
                // The key version is incremented atomically by every rotation, and a session stores the version of
                // the credential row its login actually verified. A session opened with an older key (even one whose
                // login was in flight during the rotation) no longer matches. No clocks involved.
                return agent is { IsActive: true } && agent.KeyVersion == token.SubjectVersion
                    ? new TokenSubject(TokenSubjectType.Agent, agent.Id, agent.ServerName, Roles.Agent, agent.KeyVersion)
                    : null;

            default:
                return null;
        }
    }

    // Not cancellable: once reuse is detected, the revocation must happen even if the caller disconnects.
    private async Task RevokeFamilyAfterReuseAsync(RefreshToken token, DateTimeOffset now)
    {
        var revoked = await refreshTokens.RevokeFamilyAsync(token.FamilyId, now, CancellationToken.None);
        LogRefreshTokenReuse(logger, token.SubjectType, token.SubjectId, token.FamilyId, revoked);
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Refresh token reuse detected for {SubjectType} {SubjectId}; revoked {Revoked} token(s) of family {FamilyId}")]
    private static partial void LogRefreshTokenReuse(ILogger logger, TokenSubjectType subjectType, int subjectId, Guid familyId, int revoked);
}
