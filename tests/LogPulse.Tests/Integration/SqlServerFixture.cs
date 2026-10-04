using LogPulse.Data;
using Testcontainers.MsSql;

namespace LogPulse.Tests.Integration;

/// <summary>
/// Starts one SQL Server container per test collection and applies the schema. Also hosts one shared
/// in-memory API on top of it, created on first use, so API tests do not pay the startup cost each time.
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private readonly MsSqlContainer _container =
        new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    private ApiFactory? _api;

    // Assigned in InitializeAsync, which xUnit always runs before any test in the collection.
    public IDbConnectionFactory ConnectionFactory { get; private set; } = null!;

    public string ConnectionString => _container.GetConnectionString();

    /// <summary>The shared API. Tests in a collection run one at a time, so lazy creation needs no locking.</summary>
    public ApiFactory Api => _api ??= new ApiFactory(ConnectionString);

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        ConnectionFactory = new SqlConnectionFactory(ConnectionString);
        await new DatabaseInitializer(ConnectionFactory).InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        if (_api is not null)
        {
            await _api.DisposeAsync();
        }

        await _container.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class SqlServerGroup : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "SqlServer";
}
