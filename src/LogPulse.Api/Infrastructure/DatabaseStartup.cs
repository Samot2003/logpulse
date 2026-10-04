using System.ComponentModel.DataAnnotations;
using System.Data.Common;
using LogPulse.Api.Auth;
using LogPulse.Api.Options;
using LogPulse.Core.Models;
using LogPulse.Data;
using LogPulse.Data.Daos;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace LogPulse.Api.Infrastructure;

public static partial class DatabaseStartup
{
    public const string ConnectionStringName = "LogPulse";

    // A SQL Server container needs up to a minute to accept connections, and user databases recover a bit later
    // than master after a restart. Keep retrying for a while instead of crashing on the first attempt.
    private static readonly TimeSpan StartupDeadline = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(3);

    // The same attribute the API uses: it must match the whole value (Regex.IsMatch with "$" accepts a trailing newline).
    private static readonly RegularExpressionAttribute ServerNameRule = new(FieldLimits.ServerNamePattern);

    /// <summary>Creates the database if needed, applies the schema scripts and seeds the configured users and agents.</summary>
    public static async Task InitializeDatabaseAsync(this WebApplication app, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(app);

        var seed = app.Services.GetRequiredService<IOptions<SeedOptions>>().Value;
        ValidateSeed(seed); // configuration errors fail before any retry loop

        var connectionString = GetConnectionString(app.Configuration);
        var initializer = app.Services.GetRequiredService<DatabaseInitializer>();
        await RetryUntilReachableAsync(app, async () =>
        {
            await SqlDatabaseBootstrapper.EnsureDatabaseExistsAsync(connectionString, cancellationToken);
            await initializer.InitializeAsync(cancellationToken);
        }, cancellationToken);

        await SeedAsync(app.Services, seed, cancellationToken);
    }

    public static string GetConnectionString(IConfiguration configuration) =>
        configuration.GetConnectionString(ConnectionStringName) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"Connection string '{ConnectionStringName}' is not configured.");

    private static async Task RetryUntilReachableAsync(WebApplication app, Func<Task> operation, CancellationToken cancellationToken)
    {
        var time = app.Services.GetRequiredService<TimeProvider>();
        var started = time.GetTimestamp();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await operation();
                return;
            }
            catch (DbException ex) when (time.GetElapsedTime(started) < StartupDeadline)
            {
                LogDatabaseNotReady(app.Logger, attempt, ex.Message);
                await Task.Delay(RetryDelay, time, cancellationToken);
            }
        }
    }

    private static void ValidateSeed(SeedOptions seed)
    {
        foreach (var user in seed.Users.Where(u => u.UserName.Length > 0 && u.Password.Length > 0))
        {
            if (!Roles.IsUserRole(user.Role) || user.UserName.Length > FieldLimits.UserName)
            {
                throw new InvalidOperationException(
                    $"Seed user '{user.UserName}' is invalid: role must be Viewer or Admin and the name at most {FieldLimits.UserName} characters.");
            }
        }

        foreach (var agent in seed.Agents.Where(a => a.ServerName.Length > 0 && a.ApiKey.Length > 0))
        {
            // Same rules as POST /api/agents, so a seeded agent can later be rotated or revoked through the API.
            if (agent.ServerName.Length > FieldLimits.ServerName || !ServerNameRule.IsValid(agent.ServerName))
            {
                throw new InvalidOperationException(
                    $"Seed agent '{agent.ServerName}' is invalid: use letters, digits, '.', '_' or '-', at most {FieldLimits.ServerName} characters.");
            }
        }
    }

    private static async Task SeedAsync(IServiceProvider services, SeedOptions seed, CancellationToken cancellationToken)
    {
        var users = services.GetRequiredService<IUserDao>();
        var agents = services.GetRequiredService<IAgentCredentialDao>();
        var hasher = services.GetRequiredService<IPasswordHasher<User>>();
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();

        foreach (var user in seed.Users.Where(u => u.UserName.Length > 0 && u.Password.Length > 0))
        {
            var placeholder = new User { UserName = user.UserName, PasswordHash = string.Empty, Role = user.Role };
            await users.CreateIfMissingAsync(user.UserName, hasher.HashPassword(placeholder, user.Password), user.Role, now, cancellationToken);
        }

        foreach (var agent in seed.Agents.Where(a => a.ServerName.Length > 0 && a.ApiKey.Length > 0))
        {
            await agents.CreateIfMissingAsync(agent.ServerName, Secrets.Hash(agent.ApiKey), now, cancellationToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Database not reachable yet (attempt {Attempt}), retrying: {Reason}")]
    private static partial void LogDatabaseNotReady(ILogger logger, int attempt, string reason);
}
