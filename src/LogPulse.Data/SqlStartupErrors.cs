using System.Data.Common;
using Microsoft.Data.SqlClient;

namespace LogPulse.Data;

/// <summary>Tells "SQL Server is still starting" apart from errors that waiting will not fix.</summary>
public static class SqlStartupErrors
{
    // Errors seen while a SQL Server container or service is still coming up: the network or instance cannot be
    // reached yet (-2, 2, 53, 64, 121, 233, 1225, 10053, 10054, 10060, 10061, 11001), the login or the database is not
    // available yet (18456 while the sa password is being set, 4060, 18401), or the database is still recovering
    // (922, 927, 40613).
    private static readonly HashSet<int> NotReadyNumbers =
        [-2, 2, 53, 64, 121, 233, 922, 927, 1225, 4060, 10053, 10054, 10060, 10061, 11001, 18401, 18456, 40613];

    /// <summary>
    /// True when retrying in a few seconds can succeed. A syntax error in a schema script, a missing permission or a
    /// constraint violation are not: retrying them for minutes would only hide the real problem.
    /// </summary>
    public static bool IsServerNotReady(DbException exception) => exception switch
    {
        // Connection failures do not always carry a number (0 on Linux); the socket error underneath tells.
        SqlException sql => sql.Errors.Cast<SqlError>().Any(e => IsNotReadyNumber(e.Number))
            || sql.InnerException is System.Net.Sockets.SocketException or System.ComponentModel.Win32Exception,
        _ => exception.IsTransient,
    };

    public static bool IsNotReadyNumber(int errorNumber) => NotReadyNumbers.Contains(errorNumber);
}
