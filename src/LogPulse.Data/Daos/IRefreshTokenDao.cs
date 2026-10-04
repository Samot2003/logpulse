using LogPulse.Core.Models;

namespace LogPulse.Data.Daos;

public interface IRefreshTokenDao
{
    /// <summary>Stores the first token of a new family (a new session).</summary>
    Task InsertAsync(RefreshToken token, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores a rotated token unless its family has already been revoked (even partially). The check and the insert
    /// are one atomic statement, so a revocation racing with the rotation can never leave a live token behind.
    /// Returns false if the family was revoked.
    /// </summary>
    Task<bool> TryInsertRotatedAsync(RefreshToken token, CancellationToken cancellationToken = default);

    Task<RefreshToken?> GetByHashAsync(byte[] tokenHash, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically marks the token as used if it is still unused and not revoked.
    /// Returns false when another request already used or revoked it.
    /// </summary>
    Task<bool> TryMarkUsedAsync(long id, DateTimeOffset usedAt, CancellationToken cancellationToken = default);

    /// <summary>Revokes every still-active token of the family. Returns the number revoked.</summary>
    Task<int> RevokeFamilyAsync(Guid familyId, DateTimeOffset revokedAt, CancellationToken cancellationToken = default);

    /// <summary>Revokes every still-active token of a user or agent, ending all its sessions. Returns the number revoked.</summary>
    Task<int> RevokeSubjectAsync(
        TokenSubjectType subjectType, int subjectId, DateTimeOffset revokedAt, CancellationToken cancellationToken = default);

    /// <summary>Deletes tokens that expired before the cutoff, in batches. Returns the number deleted.</summary>
    Task<int> DeleteExpiredAsync(
        DateTimeOffset cutoff, int batchSize = DataDefaults.DeleteBatchSize, CancellationToken cancellationToken = default);
}
