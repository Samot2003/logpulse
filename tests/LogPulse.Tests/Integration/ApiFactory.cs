using System.Globalization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace LogPulse.Tests.Integration;

/// <summary>Hosts the real API in memory against the test SQL Server, with seeded test users and agent.</summary>
public sealed class ApiFactory(
    string connectionString,
    int authPermitsPerMinute = 10_000,
    string signingKey = "integration-test-signing-key-0123456789abcdef",
    IReadOnlyDictionary<string, string?>? extraSettings = null) : WebApplicationFactory<Program>
{
    public const string AdminUser = "it-admin";
    public const string AdminPassword = "it-admin-password";
    public const string ViewerUser = "it-viewer";
    public const string ViewerPassword = "it-viewer-password";
    public const string SeededAgentServer = "it-seeded-agent";
    public const string SeededAgentKey = "lp_it_seeded_agent_key";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Not "Development": no Swagger and no development seed data, like production.
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:LogPulse"] = connectionString,
            ["Jwt:SigningKey"] = signingKey,
            ["RateLimiting:AuthPermitsPerMinute"] = authPermitsPerMinute.ToString(CultureInfo.InvariantCulture),
            ["Retention:Enabled"] = "false",
            ["Seed:Users:0:UserName"] = AdminUser,
            ["Seed:Users:0:Password"] = AdminPassword,
            ["Seed:Users:0:Role"] = "Admin",
            ["Seed:Users:1:UserName"] = ViewerUser,
            ["Seed:Users:1:Password"] = ViewerPassword,
            ["Seed:Users:1:Role"] = "Viewer",
            ["Seed:Agents:0:ServerName"] = SeededAgentServer,
            ["Seed:Agents:0:ApiKey"] = SeededAgentKey,
        }).AddInMemoryCollection(extraSettings ?? new Dictionary<string, string?>()));
    }
}
