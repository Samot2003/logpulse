using LogPulse.Core.Models;

namespace LogPulse.Data.Daos;

public interface IServerDao
{
    /// <summary>
    /// Registers the server if it is new, moves its last-seen time forward (never backwards) and returns it.
    /// <paramref name="seenAt"/> must be the API's receive time, not the agent's clock: a skewed agent clock
    /// would otherwise pin the server as online.
    /// </summary>
    Task<Server> UpsertAsync(string name, DateTimeOffset seenAt, CancellationToken cancellationToken = default);

    Task<Server?> GetByIdAsync(int id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Server>> GetAllAsync(CancellationToken cancellationToken = default);
}
