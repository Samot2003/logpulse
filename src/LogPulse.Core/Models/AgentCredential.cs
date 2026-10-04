namespace LogPulse.Core.Models;

/// <summary>The API key of one agent, stored only as a SHA-256 hash.</summary>
public sealed record AgentCredential
{
    public int Id { get; init; }
    public required string ServerName { get; init; }
    public required byte[] KeyHash { get; init; }

    /// <summary>Incremented on every key rotation, so sessions opened with an older key can be told apart.</summary>
    public int KeyVersion { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? RevokedAt { get; init; }

    public bool IsActive => RevokedAt is null;
}
