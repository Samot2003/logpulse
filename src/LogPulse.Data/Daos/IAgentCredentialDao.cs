using LogPulse.Core.Models;

namespace LogPulse.Data.Daos;

public interface IAgentCredentialDao
{
    Task<AgentCredential?> GetByServerNameAsync(string serverName, CancellationToken cancellationToken = default);

    Task<AgentCredential?> GetByIdAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>Creates the credential, or replaces its key hash, increments its key version and reactivates it.</summary>
    Task<AgentCredential> UpsertAsync(
        string serverName, byte[] keyHash, DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>Marks the credential as revoked. Returns it, or null if the server has no credential.</summary>
    Task<AgentCredential?> RevokeAsync(string serverName, DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>Creates the credential unless the server already has one (used for seeding). Returns true if it was created.</summary>
    Task<bool> CreateIfMissingAsync(
        string serverName, byte[] keyHash, DateTimeOffset now, CancellationToken cancellationToken = default);
}
