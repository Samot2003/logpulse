using Dapper;
using LogPulse.Core.Models;

namespace LogPulse.Data.Daos;

public sealed class SqlRefreshTokenDao(IDbConnectionFactory connectionFactory) : IRefreshTokenDao
{
    private const string Columns =
        "Id, TokenHash, FamilyId, SubjectType, SubjectId, SubjectVersion, FamilyCreatedAt, CreatedAt, ExpiresAt, UsedAt, RevokedAt";

    public async Task InsertAsync(RefreshToken token, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(token);

        const string sql = """
            INSERT INTO dbo.RefreshTokens (TokenHash, FamilyId, SubjectType, SubjectId, SubjectVersion, FamilyCreatedAt, CreatedAt, ExpiresAt)
            VALUES (@TokenHash, @FamilyId, @SubjectType, @SubjectId, @SubjectVersion, @FamilyCreatedAt, @CreatedAt, @ExpiresAt)
            """;

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(sql, Parameters(token), cancellationToken: cancellationToken));
    }

    public async Task<bool> TryInsertRotatedAsync(RefreshToken token, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(token);

        // UPDLOCK + HOLDLOCK lock the family's key range for the whole statement: a concurrent RevokeFamily
        // either runs first (and this insert sees a revoked row and inserts nothing) or waits and then revokes
        // the row inserted here. Either way no live token survives a revocation.
        const string sql = """
            INSERT INTO dbo.RefreshTokens (TokenHash, FamilyId, SubjectType, SubjectId, SubjectVersion, FamilyCreatedAt, CreatedAt, ExpiresAt)
            SELECT @TokenHash, @FamilyId, @SubjectType, @SubjectId, @SubjectVersion, @FamilyCreatedAt, @CreatedAt, @ExpiresAt
             WHERE NOT EXISTS (SELECT 1 FROM dbo.RefreshTokens WITH (UPDLOCK, HOLDLOCK)
                                WHERE FamilyId = @FamilyId AND RevokedAt IS NOT NULL)
            """;

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        var inserted = await connection.ExecuteAsync(new CommandDefinition(sql, Parameters(token), cancellationToken: cancellationToken));
        return inserted == 1;
    }

    public async Task<RefreshToken?> GetByHashAsync(byte[] tokenHash, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<RefreshToken>(new CommandDefinition(
            $"SELECT {Columns} FROM dbo.RefreshTokens WHERE TokenHash = @TokenHash",
            new { TokenHash = tokenHash },
            cancellationToken: cancellationToken));
    }

    public async Task<bool> TryMarkUsedAsync(long id, DateTimeOffset usedAt, CancellationToken cancellationToken = default)
    {
        // The WHERE clause makes this a compare-and-set: of two concurrent refreshes, only one updates the row.
        const string sql = """
            UPDATE dbo.RefreshTokens
               SET UsedAt = @UsedAt
             WHERE Id = @Id AND UsedAt IS NULL AND RevokedAt IS NULL
            """;

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        var updated = await connection.ExecuteAsync(new CommandDefinition(
            sql, new { Id = id, UsedAt = usedAt }, cancellationToken: cancellationToken));
        return updated == 1;
    }

    public async Task<int> RevokeFamilyAsync(Guid familyId, DateTimeOffset revokedAt, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        return await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE dbo.RefreshTokens SET RevokedAt = @RevokedAt WHERE FamilyId = @FamilyId AND RevokedAt IS NULL",
            new { FamilyId = familyId, RevokedAt = revokedAt },
            cancellationToken: cancellationToken));
    }

    public async Task<int> RevokeSubjectAsync(
        TokenSubjectType subjectType, int subjectId, DateTimeOffset revokedAt, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        return await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE dbo.RefreshTokens SET RevokedAt = @RevokedAt
             WHERE SubjectType = @SubjectType AND SubjectId = @SubjectId AND RevokedAt IS NULL
            """,
            new { SubjectType = (byte)subjectType, SubjectId = subjectId, RevokedAt = revokedAt },
            cancellationToken: cancellationToken));
    }

    public Task<int> DeleteExpiredAsync(
        DateTimeOffset cutoff, int batchSize = DataDefaults.DeleteBatchSize, CancellationToken cancellationToken = default) =>
        SqlBatch.DeleteInBatchesAsync(
            connectionFactory, "dbo.RefreshTokens", "ExpiresAt < @Cutoff", new { Cutoff = cutoff }, batchSize, cancellationToken);

    private static object Parameters(RefreshToken token) => new
    {
        token.TokenHash,
        token.FamilyId,
        SubjectType = (byte)token.SubjectType,
        token.SubjectId,
        token.SubjectVersion,
        token.FamilyCreatedAt,
        token.CreatedAt,
        token.ExpiresAt,
    };
}
