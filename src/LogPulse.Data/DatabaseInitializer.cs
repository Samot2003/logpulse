using System.Reflection;
using Dapper;

namespace LogPulse.Data;

/// <summary>Applies the embedded, idempotent schema scripts (<c>Schema/*.sql</c>) in name order.</summary>
public sealed class DatabaseInitializer(IDbConnectionFactory connectionFactory)
{
    private const string LockResource = "LogPulse.SchemaInitialization";
    private static readonly string ScriptPrefix = typeof(DatabaseInitializer).Namespace + ".Schema.";

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var assembly = typeof(DatabaseInitializer).Assembly;
        var scripts = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(ScriptPrefix, StringComparison.Ordinal)
                           && name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .ToList();

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);

        // Several API instances may start at once: an exclusive app lock makes them apply the scripts one at a time.
        var lockResult = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            """
            DECLARE @result INT;
            EXEC @result = sp_getapplock @Resource = @Resource, @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 60000;
            SELECT @result;
            """,
            new { Resource = LockResource },
            // Longer than @LockTimeout (60 s), so sp_getapplock reports its own result instead of a client timeout.
            commandTimeout: 90,
            cancellationToken: cancellationToken));
        if (lockResult < 0)
        {
            throw new InvalidOperationException($"Could not acquire the schema initialization lock (sp_getapplock returned {lockResult}).");
        }

        try
        {
            foreach (var script in scripts)
            {
                var sql = await ReadResourceAsync(assembly, script);
                await connection.ExecuteAsync(new CommandDefinition(sql, cancellationToken: cancellationToken));
            }
        }
        finally
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "EXEC sp_releaseapplock @Resource = @Resource, @LockOwner = 'Session'",
                new { Resource = LockResource },
                cancellationToken: CancellationToken.None));
        }
    }

    private static async Task<string> ReadResourceAsync(Assembly assembly, string name)
    {
        await using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded schema script '{name}' not found.");
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }
}
