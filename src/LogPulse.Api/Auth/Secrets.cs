using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace LogPulse.Api.Auth;

/// <summary>Random secrets (API keys, refresh tokens) and their SHA-256 hashes, which is all the database stores.</summary>
public static class Secrets
{
    private const string ApiKeyPrefix = "lp_";

    /// <summary>A new agent API key: a recognizable prefix plus 256 random bits.</summary>
    public static string NewApiKey() => ApiKeyPrefix + NewRandomToken();

    /// <summary>256 random bits, base64url-encoded.</summary>
    public static string NewRandomToken() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    /// <summary>
    /// SHA-256 of the secret. A fast hash is enough because these secrets are 256-bit random values,
    /// not user-chosen passwords (those use PasswordHasher).
    /// </summary>
    public static byte[] Hash(string secret) => SHA256.HashData(Encoding.UTF8.GetBytes(secret));

    /// <summary>Constant-time comparison, so response timing does not reveal how much of a hash matched.</summary>
    public static bool HashesMatch(byte[] expected, byte[] actual) => CryptographicOperations.FixedTimeEquals(expected, actual);
}
