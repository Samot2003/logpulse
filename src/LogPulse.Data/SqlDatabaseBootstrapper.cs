using Dapper;
using Microsoft.Data.SqlClient;

namespace LogPulse.Data;

public static class SqlDatabaseBootstrapper
{
    /// <summary>
    /// Creates the database named in the connection string if it does not exist yet, connecting through
    /// <c>master</c>. Does nothing when the connection string already targets <c>master</c>.
    /// </summary>
    public static async Task EnsureDatabaseExistsAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        var database = builder.InitialCatalog;
        if (string.IsNullOrEmpty(database) || string.Equals(database, "master", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        builder.InitialCatalog = "master";
        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        // Database names cannot be parameters in CREATE DATABASE, so QUOTENAME builds a safe identifier.
        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                IF DB_ID(@Name) IS NULL
                BEGIN
                    DECLARE @sql NVARCHAR(400) = N'CREATE DATABASE ' + QUOTENAME(@Name);
                    EXEC (@sql);
                END
                """,
                new { Name = database },
                cancellationToken: cancellationToken));
        }
        catch (SqlException ex) when (ex.Number == DatabaseAlreadyExists)
        {
            // Another instance created it between our DB_ID check and CREATE DATABASE: that is the goal anyway.
        }
    }

    private const int DatabaseAlreadyExists = 1801;
}
