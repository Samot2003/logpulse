using LogPulse.Api.Auth;
using LogPulse.Api.Options;
using LogPulse.Core.Contracts;
using LogPulse.Core.Models;
using LogPulse.Tests.Fakes;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.JsonWebTokens;

namespace LogPulse.Tests.Unit;

public class AuthServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(Now);
    private readonly InMemoryUserDao _users = new();
    private readonly InMemoryAgentCredentialDao _agents = new();
    private readonly InMemoryServerDao _servers = new();
    private readonly InMemoryRefreshTokenDao _refreshTokens = new();
    private readonly PasswordHasher<User> _hasher = new();
    private readonly AuthService _auth;

    public AuthServiceTests()
    {
        var jwt = new JwtOptions { SigningKey = "unit-test-signing-key-0123456789abcdef" };
        var tokens = new TokenService(
            _refreshTokens, _users, _agents, Microsoft.Extensions.Options.Options.Create(jwt), _time, NullLogger<TokenService>.Instance);
        _auth = new AuthService(_users, _agents, _servers, _refreshTokens, tokens, _hasher, _time);

        var bob = new User { Id = 1, UserName = "bob", PasswordHash = string.Empty, Role = Roles.Admin };
        _users.Users.Add(bob with { PasswordHash = _hasher.HashPassword(bob, "correct horse") });
    }

    [Fact]
    public async Task Login_with_the_right_password_returns_a_token_with_the_user_role()
    {
        var response = await _auth.LoginUserAsync(new UserTokenRequest { UserName = "bob", Password = "correct horse" });

        Assert.NotNull(response);
        Assert.Equal(Roles.Admin, new JsonWebToken(response.AccessToken).GetClaim("role").Value);
    }

    [Theory]
    [InlineData("bob", "wrong")]
    [InlineData("nobody", "correct horse")]
    [InlineData("bob", "Correct horse")]
    public async Task Login_fails_the_same_way_for_wrong_password_and_unknown_user(string userName, string password)
    {
        Assert.Null(await _auth.LoginUserAsync(new UserTokenRequest { UserName = userName, Password = password }));
        Assert.Empty(_refreshTokens.Tokens);
    }

    [Fact]
    public async Task Agent_with_the_right_key_gets_a_token_and_its_server_is_registered_with_the_api_clock()
    {
        var created = await _auth.CreateAgentAsync("web-01");
        _time.Advance(TimeSpan.FromMinutes(5));

        var response = await _auth.AuthenticateAgentAsync(new AgentTokenRequest { ServerName = "web-01", ApiKey = created.ApiKey });

        Assert.NotNull(response);
        var token = new JsonWebToken(response.AccessToken);
        Assert.Equal(Roles.Agent, token.GetClaim("role").Value);
        Assert.Equal("web-01", token.GetClaim("name").Value);
        var server = Assert.Single(_servers.Servers);
        Assert.Equal(Now.AddMinutes(5), server.LastSeenAt);
    }

    [Fact]
    public async Task Agent_is_rejected_with_a_wrong_key_an_unknown_server_or_a_revoked_key()
    {
        var created = await _auth.CreateAgentAsync("web-01");

        Assert.Null(await _auth.AuthenticateAgentAsync(new AgentTokenRequest { ServerName = "web-01", ApiKey = created.ApiKey + "x" }));
        Assert.Null(await _auth.AuthenticateAgentAsync(new AgentTokenRequest { ServerName = "web-02", ApiKey = created.ApiKey }));

        await _agents.RevokeAsync("web-01", Now);
        Assert.Null(await _auth.AuthenticateAgentAsync(new AgentTokenRequest { ServerName = "web-01", ApiKey = created.ApiKey }));
        Assert.Empty(_servers.Servers);
    }

    [Fact]
    public async Task Rotating_an_agent_key_ends_the_sessions_opened_with_the_old_key()
    {
        var first = await _auth.CreateAgentAsync("web-01");
        var session = await _auth.AuthenticateAgentAsync(new AgentTokenRequest { ServerName = "web-01", ApiKey = first.ApiKey });

        await _auth.CreateAgentAsync("web-01"); // the admin rotates the leaked key

        Assert.All(_refreshTokens.Tokens, t => Assert.NotNull(t.RevokedAt));
        Assert.Null(await TokensFor().RefreshAsync(session!.RefreshToken));
    }

    [Fact]
    public async Task A_login_that_read_the_old_key_while_it_was_being_rotated_gets_a_session_that_cannot_be_refreshed()
    {
        var leaked = await _auth.CreateAgentAsync("web-01");
        // The attacker's login verifies the old key, then the admin's rotation commits before the session is stored.
        _agents.AfterReadByServerName = () => _auth.CreateAgentAsync("web-01");

        var session = await _auth.AuthenticateAgentAsync(new AgentTokenRequest { ServerName = "web-01", ApiKey = leaked.ApiKey });

        Assert.NotNull(session); // the access token works for its few minutes...
        Assert.Null(await TokensFor().RefreshAsync(session.RefreshToken)); // ...but the session is dead
    }

    [Fact]
    public async Task Revoking_an_agent_blocks_new_logins_and_ends_its_sessions()
    {
        var created = await _auth.CreateAgentAsync("web-01");
        var session = await _auth.AuthenticateAgentAsync(new AgentTokenRequest { ServerName = "web-01", ApiKey = created.ApiKey });

        Assert.True(await _auth.RevokeAgentAsync("web-01"));
        Assert.False(await _auth.RevokeAgentAsync("web-99"));

        Assert.Null(await _auth.AuthenticateAgentAsync(new AgentTokenRequest { ServerName = "web-01", ApiKey = created.ApiKey }));
        Assert.Null(await TokensFor().RefreshAsync(session!.RefreshToken));
    }

    private TokenService TokensFor() => new(
        _refreshTokens,
        _users,
        _agents,
        Microsoft.Extensions.Options.Options.Create(new JwtOptions { SigningKey = "unit-test-signing-key-0123456789abcdef" }),
        _time,
        NullLogger<TokenService>.Instance);

    [Fact]
    public async Task Creating_an_agent_stores_only_the_key_hash_and_rotating_invalidates_the_old_key()
    {
        var first = await _auth.CreateAgentAsync("  web-01 ");
        var stored = Assert.Single(_agents.Credentials);
        Assert.Equal("web-01", first.ServerName);
        Assert.StartsWith("lp_", first.ApiKey, StringComparison.Ordinal);
        Assert.Equal(Secrets.Hash(first.ApiKey), stored.KeyHash);

        var second = await _auth.CreateAgentAsync("web-01");

        Assert.NotEqual(first.ApiKey, second.ApiKey);
        Assert.Null(await _auth.AuthenticateAgentAsync(new AgentTokenRequest { ServerName = "web-01", ApiKey = first.ApiKey }));
        Assert.NotNull(await _auth.AuthenticateAgentAsync(new AgentTokenRequest { ServerName = "web-01", ApiKey = second.ApiKey }));
    }
}
