namespace LogPulse.Core.Models;

public static class Roles
{
    /// <summary>A monitoring agent: may only ingest data for its own server.</summary>
    public const string Agent = "Agent";

    /// <summary>A dashboard user with read-only access.</summary>
    public const string Viewer = "Viewer";

    /// <summary>A dashboard user who can also manage agent credentials.</summary>
    public const string Admin = "Admin";

    public static bool IsUserRole(string role) => role is Viewer or Admin;
}
