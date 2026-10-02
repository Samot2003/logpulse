using LogPulse.Data;
using Testcontainers.MsSql;

namespace LogPulse.Tests.Integration;

/// <summary>Starts one SQL Server container per test collection and applies the schema.</summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private readonly MsSqlContainer _container =
        new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    public IDbConnectionFactory ConnectionFactory { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        ConnectionFactory = new SqlConnectionFactory(_container.GetConnectionString());
        await new DatabaseInitializer(ConnectionFactory).InitializeAsync();
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

[CollectionDefinition(Name)]
public sealed class SqlServerGroup : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "SqlServer";
}
