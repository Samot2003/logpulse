namespace LogPulse.Core.Models;

public enum TokenSubjectType : byte
{
    User = 0,
    Agent = 1,
}

/// <summary>
/// A single-use refresh token, stored only as a SHA-256 hash. Every rotation keeps the same
/// <see cref="FamilyId"/>, so presenting an already used token can revoke the whole chain.
/// </summary>
public sealed record RefreshToken
{
    public long Id { get; init; }
    public required byte[] TokenHash { get; init; }
    public Guid FamilyId { get; init; }
    public TokenSubjectType SubjectType { get; init; }
    public int SubjectId { get; init; }

    /// <summary>Version of the subject's credential when the session was opened (agents: key version; users: 0).</summary>
    public int SubjectVersion { get; init; }

    /// <summary>When the session started (first token of the family). Rotations keep it, so sessions cannot live forever.</summary>
    public DateTimeOffset FamilyCreatedAt { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public DateTimeOffset? UsedAt { get; init; }
    public DateTimeOffset? RevokedAt { get; init; }
}
