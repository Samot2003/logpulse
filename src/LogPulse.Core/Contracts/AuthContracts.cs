using System.ComponentModel.DataAnnotations;
using LogPulse.Core.Models;

namespace LogPulse.Core.Contracts;

public sealed class UserTokenRequest
{
    [Required]
    [StringLength(FieldLimits.UserName)]
    public string UserName { get; set; } = string.Empty;

    [Required]
    [StringLength(256)]
    public string Password { get; set; } = string.Empty;
}

public sealed class AgentTokenRequest
{
    [Required]
    [StringLength(FieldLimits.ServerName)]
    public string ServerName { get; set; } = string.Empty;

    [Required]
    [StringLength(256)]
    public string ApiKey { get; set; } = string.Empty;
}

public sealed class RefreshTokenRequest
{
    [Required]
    [StringLength(256)]
    public string RefreshToken { get; set; } = string.Empty;
}

public sealed record TokenResponse(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt,
    string TokenType = "Bearer");

public sealed class CreateAgentRequest
{
    [Required]
    [StringLength(FieldLimits.ServerName)]
    [RegularExpression(FieldLimits.ServerNamePattern, ErrorMessage = "Use letters, digits, '.', '_' or '-', starting with a letter or digit.")]
    public string ServerName { get; set; } = string.Empty;
}

/// <summary>The only time the plain API key is ever returned; the server keeps just its hash.</summary>
public sealed record CreateAgentResponse(string ServerName, string ApiKey);
