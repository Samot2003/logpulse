using LogPulse.Core.Models;

namespace LogPulse.Data.Daos;

public interface IServerDao
{
    /// <summary>Registers the server if it is new, updates its last-seen time, and returns it.</summary>
    Task<Server> UpsertAsync(string name, DateTimeOffset seenAt, CancellationToken cancellationToken = default);

    Task<Server?> GetByIdAsync(int id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Server>> GetAllAsync(CancellationToken cancellationToken = default);
}
