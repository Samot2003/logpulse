using Dapper;
using LogPulse.Core.Models;

namespace LogPulse.Data.Daos;

public sealed class SqlServerDao(IDbConnectionFactory connectionFactory) : IServerDao
{
    private const string Columns = "Id, Name, RegisteredAt, LastSeenAt";

    public async Task<Server> UpsertAsync(string name, DateTimeOffset seenAt, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        // UPDLOCK + HOLDLOCK serializes concurrent first registrations of the same name.
        const string sql = $"""
            SET XACT_ABORT ON;
            BEGIN TRAN;
            UPDATE dbo.Servers WITH (UPDLOCK, HOLDLOCK)
               SET LastSeenAt = @SeenAt
             WHERE Name = @Name;
            IF @@ROWCOUNT = 0
                INSERT INTO dbo.Servers (Name, RegisteredAt, LastSeenAt) VALUES (@Name, @SeenAt, @SeenAt);
            COMMIT;
            SELECT {Columns} FROM dbo.Servers WHERE Name = @Name;
            """;

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        return await connection.QuerySingleAsync<Server>(
            new CommandDefinition(sql, new { Name = name.Trim(), SeenAt = seenAt }, cancellationToken: cancellationToken));
    }

    public async Task<Server?> GetByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<Server>(
            new CommandDefinition($"SELECT {Columns} FROM dbo.Servers WHERE Id = @Id", new { Id = id }, cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<Server>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        var servers = await connection.QueryAsync<Server>(
            new CommandDefinition($"SELECT {Columns} FROM dbo.Servers ORDER BY Name", cancellationToken: cancellationToken));
        return servers.AsList();
    }
}
