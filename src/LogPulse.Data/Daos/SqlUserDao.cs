using Dapper;
using LogPulse.Core.Models;

namespace LogPulse.Data.Daos;

public sealed class SqlUserDao(IDbConnectionFactory connectionFactory) : IUserDao
{
    private const string Columns = "Id, UserName, PasswordHash, Role, CreatedAt";

    public async Task<User?> GetByUserNameAsync(string userName, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<User>(new CommandDefinition(
            $"SELECT {Columns} FROM dbo.Users WHERE UserName = @UserName",
            new { UserName = userName },
            cancellationToken: cancellationToken));
    }

    public async Task<User?> GetByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<User>(new CommandDefinition(
            $"SELECT {Columns} FROM dbo.Users WHERE Id = @Id", new { Id = id }, cancellationToken: cancellationToken));
    }

    public async Task<bool> CreateIfMissingAsync(
        string userName, string passwordHash, string role, DateTimeOffset createdAt, CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO dbo.Users (UserName, PasswordHash, Role, CreatedAt)
            SELECT @UserName, @PasswordHash, @Role, @CreatedAt
             WHERE NOT EXISTS (SELECT 1 FROM dbo.Users WITH (UPDLOCK, HOLDLOCK) WHERE UserName = @UserName)
            """;

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        var created = await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new { UserName = userName, PasswordHash = passwordHash, Role = role, CreatedAt = createdAt },
            cancellationToken: cancellationToken));
        return created == 1;
    }
}
