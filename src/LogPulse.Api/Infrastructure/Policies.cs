namespace LogPulse.Api.Infrastructure;

public static class Policies
{
    /// <summary>Agents sending logs and metrics.</summary>
    public const string Ingest = "Ingest";

    /// <summary>Dashboard users reading data (Viewer or Admin).</summary>
    public const string Read = "Read";

    /// <summary>Managing agent credentials.</summary>
    public const string Admin = "Admin";

    /// <summary>Rate limit for the anonymous /api/auth endpoints.</summary>
    public const string AuthRateLimit = "auth";
}
