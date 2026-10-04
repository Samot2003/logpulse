using LogPulse.Core.Models;
using LogPulse.Core.Queries;
using LogPulse.Data;
using LogPulse.Data.Daos;

namespace LogPulse.Tests.Integration;

[Collection(SqlServerGroup.Name)]
[Trait("Category", "Integration")]
public class AuthDaoTests(SqlServerFixture fixture)
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly SqlUserDao _users = new(fixture.ConnectionFactory);
    private readonly SqlAgentCredentialDao _agents = new(fixture.ConnectionFactory);
    private readonly SqlRefreshTokenDao _tokens = new(fixture.ConnectionFactory);

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private static RefreshToken NewToken(Guid familyId, DateTimeOffset expiresAt) => new()
    {
        TokenHash = Guid.NewGuid().ToByteArray().Concat(Guid.NewGuid().ToByteArray()).ToArray(),
        FamilyId = familyId,
        SubjectType = TokenSubjectType.Agent,
        SubjectId = 42,
        FamilyCreatedAt = T0,
        CreatedAt = T0,
        ExpiresAt = expiresAt,
    };

    [Fact]
    public async Task A_rotated_token_is_only_inserted_while_its_family_is_not_revoked()
    {
        var family = Guid.NewGuid();
        await _tokens.InsertAsync(NewToken(family, T0.AddDays(7)));

        var rotated = NewToken(family, T0.AddDays(7)) with { SubjectVersion = 3 };
        Assert.True(await _tokens.TryInsertRotatedAsync(rotated));
        Assert.Equal(3, (await _tokens.GetByHashAsync(rotated.TokenHash))!.SubjectVersion);

        await _tokens.RevokeFamilyAsync(family, T0);
        var late = NewToken(family, T0.AddDays(7));
        Assert.False(await _tokens.TryInsertRotatedAsync(late));
        Assert.Null(await _tokens.GetByHashAsync(late.TokenHash));
    }

    [Fact]
    public async Task Revoking_a_subject_ends_all_its_sessions_and_nothing_else()
    {
        var subjectId = Random.Shared.Next(1_000_000, int.MaxValue);
        var mine = new[] { NewToken(Guid.NewGuid(), T0.AddDays(7)), NewToken(Guid.NewGuid(), T0.AddDays(7)) }
            .Select(t => t with { SubjectId = subjectId }).ToArray();
        var someoneElse = NewToken(Guid.NewGuid(), T0.AddDays(7)) with { SubjectId = subjectId, SubjectType = TokenSubjectType.User };
        foreach (var t in mine.Append(someoneElse))
        {
            await _tokens.InsertAsync(t);
        }

        Assert.Equal(2, await _tokens.RevokeSubjectAsync(TokenSubjectType.Agent, subjectId, T0));
        Assert.Null((await _tokens.GetByHashAsync(someoneElse.TokenHash))!.RevokedAt);
    }

    [Fact]
    public async Task Revoking_an_agent_credential_keeps_the_first_revocation_time()
    {
        var server = Unique("srv");
        await _agents.UpsertAsync(server, [.. Enumerable.Repeat((byte)3, 32)], T0);

        var revoked = await _agents.RevokeAsync(server, T0.AddHours(1));
        var again = await _agents.RevokeAsync(server, T0.AddHours(2));

        Assert.Equal(T0.AddHours(1), revoked!.RevokedAt);
        Assert.Equal(T0.AddHours(1), again!.RevokedAt);
        Assert.Null(await _agents.RevokeAsync(Unique("missing"), T0));
    }

    [Fact]
    public async Task Schema_initialization_is_safe_when_several_instances_start_at_once()
    {
        var initializers = Enumerable.Range(0, 4).Select(_ => new DatabaseInitializer(fixture.ConnectionFactory).InitializeAsync());

        await Task.WhenAll(initializers);
    }

    [Fact]
    public async Task Users_are_created_once_and_found_by_name_and_id()
    {
        var name = Unique("u");

        Assert.True(await _users.CreateIfMissingAsync(name, "hash-1", Roles.Viewer, T0));
        Assert.False(await _users.CreateIfMissingAsync(name, "hash-2", Roles.Admin, T0));

        var byName = await _users.GetByUserNameAsync(name);
        Assert.NotNull(byName);
        Assert.Equal("hash-1", byName.PasswordHash);
        Assert.Equal(Roles.Viewer, byName.Role);
        Assert.Equal(byName, await _users.GetByIdAsync(byName.Id));
    }

    [Fact]
    public async Task Agent_upsert_rotates_the_key_and_reactivates_but_seeding_never_overwrites()
    {
        var server = Unique("srv");
        byte[] first = [.. Enumerable.Repeat((byte)1, 32)];
        byte[] second = [.. Enumerable.Repeat((byte)2, 32)];

        var created = await _agents.UpsertAsync(server, first, T0);
        var rotated = await _agents.UpsertAsync(server, second, T0.AddDays(1));
        var seeded = await _agents.CreateIfMissingAsync(server, first, T0.AddDays(2));

        Assert.Equal(created.Id, rotated.Id);
        Assert.Equal(second, rotated.KeyHash);
        Assert.Equal(created.KeyVersion + 1, rotated.KeyVersion);
        Assert.True(rotated.IsActive);
        Assert.False(seeded);
        Assert.Equal(second, (await _agents.GetByServerNameAsync(server))!.KeyHash);
    }

    [Fact]
    public async Task Only_one_of_many_concurrent_refreshes_can_mark_a_token_as_used()
    {
        var token = NewToken(Guid.NewGuid(), T0.AddDays(7));
        await _tokens.InsertAsync(token);
        var stored = await _tokens.GetByHashAsync(token.TokenHash);

        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => _tokens.TryMarkUsedAsync(stored!.Id, T0)));

        Assert.Single(results, r => r);
        Assert.NotNull((await _tokens.GetByHashAsync(token.TokenHash))!.UsedAt);
    }

    [Fact]
    public async Task Revoking_a_family_leaves_other_families_untouched_and_revoked_tokens_cannot_be_used()
    {
        var family = Guid.NewGuid();
        var a = NewToken(family, T0.AddDays(7));
        var b = NewToken(family, T0.AddDays(7));
        var other = NewToken(Guid.NewGuid(), T0.AddDays(7));
        foreach (var t in new[] { a, b, other })
        {
            await _tokens.InsertAsync(t);
        }

        Assert.Equal(2, await _tokens.RevokeFamilyAsync(family, T0));

        Assert.NotNull((await _tokens.GetByHashAsync(a.TokenHash))!.RevokedAt);
        Assert.Null((await _tokens.GetByHashAsync(other.TokenHash))!.RevokedAt);
        Assert.False(await _tokens.TryMarkUsedAsync((await _tokens.GetByHashAsync(b.TokenHash))!.Id, T0));
    }

    [Fact]
    public async Task Expired_tokens_are_deleted_in_batches()
    {
        var longAgo = new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var family = Guid.NewGuid();
        for (var i = 0; i < 7; i++)
        {
            await _tokens.InsertAsync(NewToken(family, longAgo.AddMinutes(i)));
        }

        var keep = NewToken(family, longAgo.AddYears(10));
        await _tokens.InsertAsync(keep);

        Assert.Equal(7, await _tokens.DeleteExpiredAsync(longAgo.AddDays(1), batchSize: 3));
        Assert.NotNull(await _tokens.GetByHashAsync(keep.TokenHash));
    }
}

[Collection(SqlServerGroup.Name)]
[Trait("Category", "Integration")]
public class BatchWriteTests(SqlServerFixture fixture)
{
    private readonly SqlServerDao _servers = new(fixture.ConnectionFactory);
    private readonly SqlLogDao _logs = new(fixture.ConnectionFactory);
    private readonly SqlMetricDao _metrics = new(fixture.ConnectionFactory);

    [Fact]
    public async Task A_thousand_log_entries_are_inserted_across_several_multi_row_commands()
    {
        var t0 = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
        var server = await _servers.UpsertAsync($"bulk-{Guid.NewGuid():N}", t0);
        var entries = Enumerable.Range(0, 1000).Select(i => new LogEntry
        {
            ServerId = server.Id,
            Timestamp = t0.AddSeconds(i),
            Severity = (LogSeverity)(i % 4),
            Source = "bulk",
            Message = $"line {i}",
            Exception = i % 10 == 0 ? "stack" : null,
        }).ToList();

        Assert.Equal(1000, await _logs.InsertBatchAsync(entries));

        var page = await _logs.QueryAsync(new LogQuery { ServerId = server.Id, PageSize = 1 });
        Assert.Equal(1000, page.TotalCount);
        Assert.Equal("line 999", page.Items.Single().Message);
        // Line 990: severity 990 % 4 = Error, and every tenth line carries an exception.
        var withException = await _logs.QueryAsync(new LogQuery { ServerId = server.Id, MinSeverity = LogSeverity.Error, Search = "line 990" });
        Assert.Equal("stack", withException.Items.Single().Exception);
    }

    [Fact]
    public async Task Old_rows_are_deleted_in_several_batches()
    {
        var longAgo = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var server = await _servers.UpsertAsync($"purge-{Guid.NewGuid():N}", longAgo);
        await _logs.InsertBatchAsync(Enumerable.Range(0, 20)
            .Select(i => new LogEntry { ServerId = server.Id, Timestamp = longAgo.AddMinutes(i), Source = "old", Message = "old" })
            .ToList());

        // The purge is global on the shared database, so other tests may add old rows: at least ours must go.
        Assert.True(await _logs.DeleteOlderThanAsync(longAgo.AddDays(1), batchSize: 6) >= 20);
        Assert.Equal(0, (await _logs.QueryAsync(new LogQuery { ServerId = server.Id })).TotalCount);
    }

    [Fact]
    public async Task Metric_range_returns_the_newest_samples_oldest_first_when_capped()
    {
        var t0 = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
        var server = await _servers.UpsertAsync($"range-{Guid.NewGuid():N}", t0);
        await _metrics.InsertBatchAsync(Enumerable.Range(0, 10)
            .Select(i => new MetricSample { ServerId = server.Id, Timestamp = t0.AddMinutes(i), CpuPercent = i })
            .ToList());

        var capped = await _metrics.GetRangeAsync(server.Id, t0, t0.AddHours(1), maxRows: 3);

        Assert.Equal([7d, 8d, 9d], capped.Select(s => s.CpuPercent));
    }
}
