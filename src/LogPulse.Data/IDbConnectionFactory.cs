using System.Data.Common;

namespace LogPulse.Data;

public interface IDbConnectionFactory
{
    /// <summary>Creates and opens a new connection. The caller owns and disposes it.</summary>
    Task<DbConnection> OpenAsync(CancellationToken cancellationToken = default);
}
