using LogPulse.Api.Auth;
using LogPulse.Api.Options;
using LogPulse.Core.Models;
using LogPulse.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace LogPulse.Tests.Unit;

public class TokenServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly JwtOptions Jwt = new()
    {
        Issuer = "test-issuer",
        Audience = "test-audience",
        SigningKey = "unit-test-signing-key-0123456789abcdef",
        AccessTokenMinutes = 15,
        RefreshTokenDays = 7,
        MaxSessionDays = 30,
    };

    private readonly FakeTimeProvider _time = new(Now);
    private readonly InMemoryRefreshTokenDao _refreshTokens = new();
    private readonly InMemoryUserDao _users = new();
    private readonly InMemoryAgentCredentialDao _agents = new();
    private readonly TokenService _service;

    public TokenServiceTests()
    {
        _service = new TokenService(
            _refreshTokens, _users, _agents, Microsoft.Extensions.Options.Options.Create(Jwt), _time, NullLogger<TokenService>.Instance);
        _users.Users.Add(new User { Id = 1, UserName = "alice", PasswordHash = "x", Role = Roles.Viewer });
    }

    private static TokenSubject Alice => new(TokenSubjectType.User, 1, "alice", Roles.Viewer, Version: 0);

    [Fact]
    public async Task Issue_creates_a_signed_jwt_with_subject_claims_and_a_15_minute_lifetime()
    {
        var response = await _service.IssueAsync(Alice);

        var parameters = JwtSetup.ValidationParameters(Jwt);
        parameters.ValidateLifetime = false; // the fake clock is in the past relative to the real one
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(response.AccessToken, parameters);

        Assert.True(result.IsValid, result.Exception?.Message);
        Assert.Equal("user:1", result.Claims[JwtSetup.SubjectClaim]);
        Assert.Equal("alice", result.Claims[JwtSetup.NameClaim]);
        Assert.Equal(Roles.Viewer, result.Claims[JwtSetup.RoleClaim]);
        Assert.Equal(Now.AddMinutes(15), response.AccessTokenExpiresAt);
        Assert.Equal(Now.AddMinutes(15).UtcDateTime, ((JsonWebToken)result.SecurityToken).ValidTo);
        Assert.Equal(Now.AddDays(7), response.RefreshTokenExpiresAt);
    }

    [Fact]
    public async Task Access_token_signed_with_another_key_is_rejected()
    {
        var response = await _service.IssueAsync(Alice);

        var parameters = JwtSetup.ValidationParameters(new JwtOptions { SigningKey = "another-key-that-is-long-enough-0123456789", Issuer = Jwt.Issuer, Audience = Jwt.Audience });
        parameters.ValidateLifetime = false;
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(response.AccessToken, parameters);

        Assert.False(result.IsValid);
        Assert.IsType<SecurityTokenSignatureKeyNotFoundException>(result.Exception);
    }

    [Fact]
    public async Task Only_the_hash_of_the_refresh_token_is_stored()
    {
        var response = await _service.IssueAsync(Alice);

        var stored = Assert.Single(_refreshTokens.Tokens);
        Assert.Equal(Secrets.Hash(response.RefreshToken), stored.TokenHash);
        Assert.DoesNotContain(response.RefreshToken, Convert.ToBase64String(stored.TokenHash), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refresh_rotates_the_token_within_the_same_family()
    {
        var first = await _service.IssueAsync(Alice);

        var second = await _service.RefreshAsync(first.RefreshToken);

        Assert.NotNull(second);
        Assert.NotEqual(first.RefreshToken, second.RefreshToken);
        Assert.Equal(2, _refreshTokens.Tokens.Count);
        Assert.Single(_refreshTokens.Tokens.Select(t => t.FamilyId).Distinct());
        Assert.NotNull(_refreshTokens.Tokens[0].UsedAt);
        Assert.Null(_refreshTokens.Tokens[1].UsedAt);
    }

    [Fact]
    public async Task Reusing_a_rotated_token_revokes_the_whole_family()
    {
        var first = await _service.IssueAsync(Alice);
        var second = await _service.RefreshAsync(first.RefreshToken);

        // An attacker (or a buggy client) replays the old token.
        var replay = await _service.RefreshAsync(first.RefreshToken);

        Assert.Null(replay);
        Assert.All(_refreshTokens.Tokens, t => Assert.NotNull(t.RevokedAt));
        // The legitimate latest token stops working too, forcing a new login.
        Assert.Null(await _service.RefreshAsync(second!.RefreshToken));
    }

    [Fact]
    public async Task Expired_refresh_token_is_rejected()
    {
        var response = await _service.IssueAsync(Alice);
        _time.Advance(TimeSpan.FromDays(7) + TimeSpan.FromSeconds(1));

        Assert.Null(await _service.RefreshAsync(response.RefreshToken));
    }

    [Fact]
    public async Task Unknown_refresh_token_is_rejected()
    {
        Assert.Null(await _service.RefreshAsync(Secrets.NewRandomToken()));
    }

    [Fact]
    public async Task Refresh_uses_the_current_role_from_the_database()
    {
        var response = await _service.IssueAsync(Alice);
        _users.Users[0] = _users.Users[0] with { Role = Roles.Admin };

        var refreshed = await _service.RefreshAsync(response.RefreshToken);

        var token = new JsonWebToken(refreshed!.AccessToken);
        Assert.Equal(Roles.Admin, token.GetClaim(JwtSetup.RoleClaim).Value);
    }

    [Fact]
    public async Task Refresh_fails_and_revokes_the_family_once_the_agent_key_is_revoked()
    {
        var credential = await _agents.UpsertAsync("web-01", Secrets.Hash("key"), Now);
        // Same key version, so only the revocation (IsActive) can make the refresh fail.
        var response = await _service.IssueAsync(
            new TokenSubject(TokenSubjectType.Agent, credential.Id, "web-01", Roles.Agent, credential.KeyVersion));
        await _agents.RevokeAsync("web-01", Now);

        Assert.Null(await _service.RefreshAsync(response.RefreshToken));
        Assert.All(_refreshTokens.Tokens, t => Assert.NotNull(t.RevokedAt));
    }

    [Fact]
    public async Task A_revocation_racing_with_a_rotation_leaves_no_live_token()
    {
        var first = await _service.IssueAsync(Alice);
        // Reuse detection or a logout revokes the family after we won the compare-and-set but before the insert.
        _refreshTokens.BeforeInsertRotated = () => _refreshTokens.RevokeFamilyAsync(_refreshTokens.Tokens[0].FamilyId, Now);

        var rotated = await _service.RefreshAsync(first.RefreshToken);

        Assert.Null(rotated);
        Assert.All(_refreshTokens.Tokens, t => Assert.NotNull(t.RevokedAt));
    }

    [Fact]
    public async Task An_agent_session_stops_working_once_the_key_version_changes()
    {
        var credential = await _agents.UpsertAsync("web-01", Secrets.Hash("key-1"), Now);
        var session = await _service.IssueAsync(
            new TokenSubject(TokenSubjectType.Agent, credential.Id, "web-01", Roles.Agent, credential.KeyVersion));
        var refreshed = await _service.RefreshAsync(session.RefreshToken);
        Assert.NotNull(refreshed);

        await _agents.UpsertAsync("web-01", Secrets.Hash("key-2"), Now); // rotation: version 1 -> 2

        Assert.Null(await _service.RefreshAsync(refreshed.RefreshToken));
    }

    [Fact]
    public async Task Sessions_have_an_absolute_lifetime_even_when_refreshed_regularly()
    {
        var token = (await _service.IssueAsync(Alice)).RefreshToken;

        // Refreshing every 6 days keeps each refresh token valid (7 days), but the session ends after 30 days.
        for (var day = 6; day < 30; day += 6)
        {
            _time.Advance(TimeSpan.FromDays(6));
            var refreshed = await _service.RefreshAsync(token);
            Assert.NotNull(refreshed);
            Assert.True(refreshed.RefreshTokenExpiresAt <= Now.AddDays(30));
            token = refreshed.RefreshToken;
        }

        _time.Advance(TimeSpan.FromDays(6)); // day 30
        Assert.Null(await _service.RefreshAsync(token));
    }

    [Fact]
    public async Task Revoke_logs_out_every_token_of_the_family()
    {
        var first = await _service.IssueAsync(Alice);
        var second = await _service.RefreshAsync(first.RefreshToken);

        await _service.RevokeAsync(second!.RefreshToken);

        Assert.Null(await _service.RefreshAsync(second.RefreshToken));
        Assert.All(_refreshTokens.Tokens, t => Assert.NotNull(t.RevokedAt));
    }
}
