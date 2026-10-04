using LogPulse.Core.Models;
using LogPulse.Core.Queries;
using LogPulse.Data.Daos;

namespace LogPulse.Tests.Fakes;

// Minimal in-memory DAOs for unit-testing services without a database. They mimic the SQL semantics
// that the services rely on (e.g. TryMarkUsed is a compare-and-set), nothing more.

internal static class SqlText
{
    /// <summary>Equality as SQL Server's default collation sees it: case-insensitive, trailing spaces ignored.</summary>
    public static bool SqlEquals(this string a, string b) =>
        string.Equals(a.TrimEnd(), b.TrimEnd(), StringComparison.OrdinalIgnoreCase);
}

internal sealed class InMemoryUserDao : IUserDao
{
    public List<User> Users { get; } = [];

    public Task<User?> GetByUserNameAsync(string userName, CancellationToken cancellationToken = default) =>
        Task.FromResult(Users.SingleOrDefault(u => u.UserName.SqlEquals(userName)));

    public Task<User?> GetByIdAsync(int id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Users.SingleOrDefault(u => u.Id == id));

    public Task<bool> CreateIfMissingAsync(
        string userName, string passwordHash, string role, DateTimeOffset createdAt, CancellationToken cancellationToken = default)
    {
        if (Users.Any(u => u.UserName.SqlEquals(userName)))
        {
            return Task.FromResult(false);
        }

        Users.Add(new User { Id = Users.Count + 1, UserName = userName, PasswordHash = passwordHash, Role = role, CreatedAt = createdAt });
        return Task.FromResult(true);
    }
}

internal sealed class InMemoryAgentCredentialDao : IAgentCredentialDao
{
    public List<AgentCredential> Credentials { get; } = [];

    /// <summary>Runs right after a credential is read by name, to simulate a key rotation racing with a login.</summary>
    public Func<Task>? AfterReadByServerName { get; set; }

    public async Task<AgentCredential?> GetByServerNameAsync(string serverName, CancellationToken cancellationToken = default)
    {
        var credential = Credentials.SingleOrDefault(c => c.ServerName.SqlEquals(serverName));
        if (AfterReadByServerName is { } hook)
        {
            AfterReadByServerName = null; // once, so the hook itself can rotate without recursion
            await hook();
        }

        return credential;
    }

    public Task<AgentCredential?> GetByIdAsync(int id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Credentials.SingleOrDefault(c => c.Id == id));

    public Task<AgentCredential> UpsertAsync(string serverName, byte[] keyHash, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var index = Credentials.FindIndex(c => c.ServerName.SqlEquals(serverName));
        var credential = index < 0
            ? new AgentCredential { Id = Credentials.Count + 1, ServerName = serverName, KeyHash = keyHash, KeyVersion = 1, CreatedAt = now }
            : Credentials[index] with { KeyHash = keyHash, KeyVersion = Credentials[index].KeyVersion + 1, CreatedAt = now, RevokedAt = null };

        if (index < 0)
        {
            Credentials.Add(credential);
        }
        else
        {
            Credentials[index] = credential;
        }

        return Task.FromResult(credential);
    }

    public async Task<bool> CreateIfMissingAsync(string serverName, byte[] keyHash, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        if (Credentials.Any(c => c.ServerName.SqlEquals(serverName)))
        {
            return false;
        }

        await UpsertAsync(serverName, keyHash, now, cancellationToken);
        return true;
    }

    public Task<AgentCredential?> RevokeAsync(string serverName, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var index = Credentials.FindIndex(c => c.ServerName.SqlEquals(serverName));
        if (index < 0)
        {
            return Task.FromResult<AgentCredential?>(null);
        }

        Credentials[index] = Credentials[index] with { RevokedAt = Credentials[index].RevokedAt ?? now };
        return Task.FromResult<AgentCredential?>(Credentials[index]);
    }
}

internal sealed class InMemoryRefreshTokenDao : IRefreshTokenDao
{
    public List<RefreshToken> Tokens { get; } = [];

    /// <summary>Runs just before a rotated token is inserted, to simulate a request racing with the rotation.</summary>
    public Func<Task>? BeforeInsertRotated { get; set; }

    public Task InsertAsync(RefreshToken token, CancellationToken cancellationToken = default)
    {
        Tokens.Add(token with { Id = Tokens.Count + 1 });
        return Task.CompletedTask;
    }

    public async Task<bool> TryInsertRotatedAsync(RefreshToken token, CancellationToken cancellationToken = default)
    {
        if (BeforeInsertRotated is not null)
        {
            await BeforeInsertRotated();
        }

        if (Tokens.Any(t => t.FamilyId == token.FamilyId && t.RevokedAt is not null))
        {
            return false;
        }

        Tokens.Add(token with { Id = Tokens.Count + 1 });
        return true;
    }

    public Task<int> RevokeSubjectAsync(
        TokenSubjectType subjectType, int subjectId, DateTimeOffset revokedAt, CancellationToken cancellationToken = default) =>
        RevokeWhere(t => t.SubjectType == subjectType && t.SubjectId == subjectId, revokedAt);

    public Task<RefreshToken?> GetByHashAsync(byte[] tokenHash, CancellationToken cancellationToken = default) =>
        Task.FromResult(Tokens.SingleOrDefault(t => t.TokenHash.AsSpan().SequenceEqual(tokenHash)));

    public Task<bool> TryMarkUsedAsync(long id, DateTimeOffset usedAt, CancellationToken cancellationToken = default)
    {
        var index = Tokens.FindIndex(t => t.Id == id && t.UsedAt is null && t.RevokedAt is null);
        if (index < 0)
        {
            return Task.FromResult(false);
        }

        Tokens[index] = Tokens[index] with { UsedAt = usedAt };
        return Task.FromResult(true);
    }

    public Task<int> RevokeFamilyAsync(Guid familyId, DateTimeOffset revokedAt, CancellationToken cancellationToken = default) =>
        RevokeWhere(t => t.FamilyId == familyId, revokedAt);

    private Task<int> RevokeWhere(Func<RefreshToken, bool> match, DateTimeOffset revokedAt)
    {
        var revoked = 0;
        for (var i = 0; i < Tokens.Count; i++)
        {
            if (match(Tokens[i]) && Tokens[i].RevokedAt is null)
            {
                Tokens[i] = Tokens[i] with { RevokedAt = revokedAt };
                revoked++;
            }
        }

        return Task.FromResult(revoked);
    }

    public Task<int> DeleteExpiredAsync(DateTimeOffset cutoff, int batchSize = DataDefaults.DeleteBatchSize, CancellationToken cancellationToken = default) =>
        Task.FromResult(Tokens.RemoveAll(t => t.ExpiresAt < cutoff));
}

internal sealed class InMemoryServerDao : IServerDao
{
    public List<Server> Servers { get; } = [];

    public Task<Server> UpsertAsync(string name, DateTimeOffset seenAt, CancellationToken cancellationToken = default)
    {
        name = name.Trim();
        var index = Servers.FindIndex(s => s.Name.SqlEquals(name));
        var server = index < 0
            ? new Server(Servers.Count + 1, name, seenAt, seenAt)
            : Servers[index] with { LastSeenAt = seenAt > Servers[index].LastSeenAt ? seenAt : Servers[index].LastSeenAt };

        if (index < 0)
        {
            Servers.Add(server);
        }
        else
        {
            Servers[index] = server;
        }

        return Task.FromResult(server);
    }

    public Task<Server?> GetByIdAsync(int id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Servers.SingleOrDefault(s => s.Id == id));

    public Task<IReadOnlyList<Server>> GetAllAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Server>>(Servers.OrderBy(s => s.Name).ToList());
}

internal sealed class RecordingLogDao : ILogDao
{
    public List<LogEntry> Inserted { get; } = [];
    public List<(DateTimeOffset Cutoff, int BatchSize)> Deletes { get; } = [];

    public Task<int> InsertBatchAsync(IReadOnlyCollection<LogEntry> entries, CancellationToken cancellationToken = default)
    {
        Inserted.AddRange(entries);
        return Task.FromResult(entries.Count);
    }

    public Task<PagedResult<LogEntry>> QueryAsync(LogQuery query, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, int batchSize = DataDefaults.DeleteBatchSize, CancellationToken cancellationToken = default)
    {
        Deletes.Add((cutoff, batchSize));
        return Task.FromResult(3);
    }
}

internal sealed class RecordingMetricDao : IMetricDao
{
    public List<MetricSample> Inserted { get; } = [];
    public List<(DateTimeOffset Cutoff, int BatchSize)> Deletes { get; } = [];

    public Task<int> InsertBatchAsync(IReadOnlyCollection<MetricSample> samples, CancellationToken cancellationToken = default)
    {
        Inserted.AddRange(samples);
        return Task.FromResult(samples.Count);
    }

    public Task<IReadOnlyList<MetricSample>> GetRangeAsync(
        int serverId, DateTimeOffset from, DateTimeOffset to, int maxRows = IMetricDao.DefaultMaxRangeRows, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<MetricSample>> GetLatestPerServerAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, int batchSize = DataDefaults.DeleteBatchSize, CancellationToken cancellationToken = default)
    {
        Deletes.Add((cutoff, batchSize));
        return Task.FromResult(5);
    }
}
