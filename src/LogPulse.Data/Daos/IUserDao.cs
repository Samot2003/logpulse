using LogPulse.Core.Models;

namespace LogPulse.Data.Daos;

public interface IUserDao
{
    Task<User?> GetByUserNameAsync(string userName, CancellationToken cancellationToken = default);

    Task<User?> GetByIdAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>Creates the user unless the name already exists (used for seeding). Returns true if it was created.</summary>
    Task<bool> CreateIfMissingAsync(
        string userName, string passwordHash, string role, DateTimeOffset createdAt, CancellationToken cancellationToken = default);
}
