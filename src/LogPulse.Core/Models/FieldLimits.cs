namespace LogPulse.Core.Models;

/// <summary>Maximum lengths of stored text fields. They mirror the column sizes in the SQL schema.</summary>
public static class FieldLimits
{
    public const int ServerName = 128;

    /// <summary>Host-name-like server names: they appear in URLs (e.g. DELETE /api/agents/{serverName}).</summary>
    public const string ServerNamePattern = "^[A-Za-z0-9][A-Za-z0-9._-]*$";
    public const int UserName = 64;
    public const int LogSource = 256;
    public const int LogMessage = 4000;

    /// <summary>The column is NVARCHAR(MAX); this cap keeps a single runaway stack trace from filling the disk.</summary>
    public const int LogException = 32_000;
}
