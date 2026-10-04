using LogPulse.Core.Contracts;
using LogPulse.Core.Models;
using LogPulse.Data.Daos;
using Microsoft.AspNetCore.Identity;

namespace LogPulse.Api.Auth;

/// <summary>Checks user passwords and agent API keys and issues token pairs.</summary>
public sealed class AuthService
{
    private readonly IUserDao _users;
    private readonly IAgentCredentialDao _agentCredentials;
    private readonly IServerDao _servers;
    private readonly IRefreshTokenDao _refreshTokens;
    private readonly TokenService _tokens;
    private readonly IPasswordHasher<User> _passwordHasher;
    private readonly TimeProvider _time;

    // Verified when the user does not exist, so unknown and known user names take about the same time.
    private readonly User _dummyUser;

    private static readonly byte[] DummyKeyHash = Secrets.Hash("unknown-agent");

    public AuthService(
        IUserDao users,
        IAgentCredentialDao agentCredentials,
        IServerDao servers,
        IRefreshTokenDao refreshTokens,
        TokenService tokens,
        IPasswordHasher<User> passwordHasher,
        TimeProvider time)
    {
        _users = users;
        _agentCredentials = agentCredentials;
        _servers = servers;
        _refreshTokens = refreshTokens;
        _tokens = tokens;
        _passwordHasher = passwordHasher;
        _time = time;

        var placeholder = new User { UserName = string.Empty, PasswordHash = string.Empty, Role = Roles.Viewer };
        _dummyUser = placeholder with { PasswordHash = passwordHasher.HashPassword(placeholder, Secrets.NewRandomToken()) };
    }

    /// <summary>Returns a token pair, or null if the credentials are wrong. Callers must not reveal which part failed.</summary>
    public async Task<TokenResponse?> LoginUserAsync(UserTokenRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var user = await _users.GetByUserNameAsync(request.UserName, cancellationToken);
        var result = _passwordHasher.VerifyHashedPassword(user ?? _dummyUser, (user ?? _dummyUser).PasswordHash, request.Password);
        if (user is null || result == PasswordVerificationResult.Failed)
        {
            return null;
        }

        return await _tokens.IssueAsync(
            new TokenSubject(TokenSubjectType.User, user.Id, user.UserName, user.Role, Version: 0), cancellationToken);
    }

    /// <summary>
    /// Returns a token pair for the agent, or null if the key is wrong or revoked. Also registers the server
    /// (or refreshes its last-seen time) using the API's own clock.
    /// </summary>
    public async Task<TokenResponse?> AuthenticateAgentAsync(AgentTokenRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var credential = await _agentCredentials.GetByServerNameAsync(request.ServerName, cancellationToken);
        var keyMatches = Secrets.HashesMatch(credential?.KeyHash ?? DummyKeyHash, Secrets.Hash(request.ApiKey));
        if (credential is not { IsActive: true } || !keyMatches)
        {
            return null;
        }

        await _servers.UpsertAsync(credential.ServerName, _time.GetUtcNow(), cancellationToken);
        // The session is bound to the key version of the row whose hash we just verified: if the key is rotated
        // while this login is in flight, the version no longer matches and the first refresh is rejected.
        return await _tokens.IssueAsync(
            new TokenSubject(TokenSubjectType.Agent, credential.Id, credential.ServerName, Roles.Agent, credential.KeyVersion),
            cancellationToken);
    }

    /// <summary>
    /// Creates a credential for the server, or rotates its key. Rotating ends every session opened with the old
    /// key: their refresh tokens are revoked here, and TokenService also rejects any session bound to an older key
    /// version (which covers logins that were in flight). The plain key is returned only here.
    /// </summary>
    public async Task<CreateAgentResponse> CreateAgentAsync(string serverName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serverName);

        var apiKey = Secrets.NewApiKey();
        var now = _time.GetUtcNow();
        var credential = await _agentCredentials.UpsertAsync(serverName.Trim(), Secrets.Hash(apiKey), now, cancellationToken);
        await _refreshTokens.RevokeSubjectAsync(TokenSubjectType.Agent, credential.Id, now, CancellationToken.None);
        return new CreateAgentResponse(credential.ServerName, apiKey);
    }

    /// <summary>
    /// Revokes the agent's key and ends all its sessions. Access tokens already issued stay valid until they
    /// expire (at most the access token lifetime). Returns false if the server has no credential.
    /// </summary>
    public async Task<bool> RevokeAgentAsync(string serverName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serverName);

        var now = _time.GetUtcNow();
        var credential = await _agentCredentials.RevokeAsync(serverName.Trim(), now, cancellationToken);
        if (credential is null)
        {
            return false;
        }

        await _refreshTokens.RevokeSubjectAsync(TokenSubjectType.Agent, credential.Id, now, CancellationToken.None);
        return true;
    }
}
