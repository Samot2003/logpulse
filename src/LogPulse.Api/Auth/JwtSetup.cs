using System.Text;
using LogPulse.Api.Options;
using Microsoft.IdentityModel.Tokens;

namespace LogPulse.Api.Auth;

/// <summary>Claim names and validation rules shared by token creation and the JWT bearer handler.</summary>
public static class JwtSetup
{
    public const string SubjectClaim = "sub";
    public const string NameClaim = "name";
    public const string RoleClaim = "role";
    public const string TokenIdClaim = "jti";

    public static SymmetricSecurityKey SigningKey(JwtOptions options) => new(Encoding.UTF8.GetBytes(options.SigningKey));

    public static TokenValidationParameters ValidationParameters(JwtOptions options) => new()
    {
        ValidateIssuer = true,
        ValidIssuer = options.Issuer,
        ValidateAudience = true,
        ValidAudience = options.Audience,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = SigningKey(options),
        // Only the algorithm we sign with; rejects "none" and algorithm-confusion attacks.
        ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
        ValidateLifetime = true,
        RequireExpirationTime = true,
        ClockSkew = TimeSpan.FromSeconds(30),
        NameClaimType = NameClaim,
        RoleClaimType = RoleClaim,
    };
}
