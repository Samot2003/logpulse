using System.Reflection;
using Dapper;

namespace LogPulse.Data;

/// <summary>Applies the embedded, idempotent schema scripts in name order.</summary>
public sealed class DatabaseInitializer(IDbConnectionFactory connectionFactory)
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var assembly = typeof(DatabaseInitializer).Assembly;
        var scripts = assembly.GetManifestResourceNames()
            .Where(name => name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal);

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        foreach (var script in scripts)
        {
            var sql = await ReadResourceAsync(assembly, script);
            await connection.ExecuteAsync(new CommandDefinition(sql, cancellationToken: cancellationToken));
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
