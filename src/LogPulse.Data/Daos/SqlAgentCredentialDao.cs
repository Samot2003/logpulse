using Dapper;
using LogPulse.Core.Models;

namespace LogPulse.Data.Daos;

public sealed class SqlAgentCredentialDao(IDbConnectionFactory connectionFactory) : IAgentCredentialDao
{
    private const string Columns = "Id, ServerName, KeyHash, KeyVersion, CreatedAt, RevokedAt";

    public async Task<AgentCredential?> GetByServerNameAsync(string serverName, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<AgentCredential>(new CommandDefinition(
            $"SELECT {Columns} FROM dbo.AgentCredentials WHERE ServerName = @ServerName",
            new { ServerName = serverName },
            cancellationToken: cancellationToken));
    }

    public async Task<AgentCredential?> GetByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<AgentCredential>(new CommandDefinition(
            $"SELECT {Columns} FROM dbo.AgentCredentials WHERE Id = @Id", new { Id = id }, cancellationToken: cancellationToken));
    }

    public async Task<AgentCredential> UpsertAsync(
        string serverName, byte[] keyHash, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        const string sql = $"""
            SET XACT_ABORT ON;
            BEGIN TRAN;
            UPDATE dbo.AgentCredentials WITH (UPDLOCK, HOLDLOCK)
               SET KeyHash = @KeyHash, KeyVersion = KeyVersion + 1, CreatedAt = @Now, RevokedAt = NULL
             WHERE ServerName = @ServerName;
            IF @@ROWCOUNT = 0
                INSERT INTO dbo.AgentCredentials (ServerName, KeyHash, CreatedAt) VALUES (@ServerName, @KeyHash, @Now);
            COMMIT;
            SELECT {Columns} FROM dbo.AgentCredentials WHERE ServerName = @ServerName;
            """;

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        return await connection.QuerySingleAsync<AgentCredential>(new CommandDefinition(
            sql, new { ServerName = serverName, KeyHash = keyHash, Now = now }, cancellationToken: cancellationToken));
    }

    public async Task<AgentCredential?> RevokeAsync(string serverName, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        // Keeps the first revocation time if it was already revoked.
        var sql = $"""
            UPDATE dbo.AgentCredentials SET RevokedAt = COALESCE(RevokedAt, @Now) WHERE ServerName = @ServerName;
            SELECT {Columns} FROM dbo.AgentCredentials WHERE ServerName = @ServerName;
            """;

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<AgentCredential>(new CommandDefinition(
            sql, new { ServerName = serverName, Now = now }, cancellationToken: cancellationToken));
    }

    public async Task<bool> CreateIfMissingAsync(
        string serverName, byte[] keyHash, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO dbo.AgentCredentials (ServerName, KeyHash, CreatedAt)
            SELECT @ServerName, @KeyHash, @Now
             WHERE NOT EXISTS (SELECT 1 FROM dbo.AgentCredentials WITH (UPDLOCK, HOLDLOCK) WHERE ServerName = @ServerName)
            """;

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        var created = await connection.ExecuteAsync(new CommandDefinition(
            sql, new { ServerName = serverName, KeyHash = keyHash, Now = now }, cancellationToken: cancellationToken));
        return created == 1;
    }
}
