namespace LogPulse.Data.Daos;

/// <summary>Defaults that are part of the DAO contracts.</summary>
public static class DataDefaults
{
    /// <summary>
    /// Rows per batched DELETE. SQL Server tries to escalate to a table lock at about 5000 locks per statement,
    /// so batches stay below that and never block ingestion or the dashboard.
    /// </summary>
    public const int DeleteBatchSize = 4000;

    /// <summary>Upper bound for configurable batch sizes, for the same reason.</summary>
    public const int MaxDeleteBatchSize = 4900;
}
