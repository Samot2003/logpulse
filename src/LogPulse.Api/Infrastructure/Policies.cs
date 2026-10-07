namespace LogPulse.Api.Infrastructure;

public static class Policies
{
    /// <summary>Agents sending logs and metrics.</summary>
    public const string Ingest = "Ingest";

    /// <summary>Dashboard users reading data (Viewer or Admin).</summary>
    public const string Read = "Read";

    /// <summary>Managing agent credentials.</summary>
    public const string Admin = "Admin";

    /// <summary>Rate limit for the anonymous /api/auth endpoints, per client IP.</summary>
    public const string AuthRateLimit = "auth";

    /// <summary>Rate limit for ingestion, per agent.</summary>
    public const string IngestRateLimit = "ingest";

    /// <summary>Rate limit for the query endpoints, per user.</summary>
    public const string ReadRateLimit = "read";
}
