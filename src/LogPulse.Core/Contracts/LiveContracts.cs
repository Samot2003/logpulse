using LogPulse.Core.Models;

namespace LogPulse.Core.Contracts;

/// <summary>The API's real-time SignalR hub. Dashboard servers connect to it with a Viewer or Admin token.</summary>
public static class LiveHubRoute
{
    public const string Path = "/hubs/live";
}

/// <summary>
/// Messages the API pushes to every connected viewer right after storing an agent's batch.
/// The method names are the SignalR method names, so the dashboard subscribes with <c>nameof</c>.
/// </summary>
public interface ILiveClient
{
    Task LogsReceived(LogsReceivedEvent received);

    Task MetricsReceived(MetricsReceivedEvent received);
}

/// <summary>
/// A batch of log entries was stored. Only a summary travels: viewers that show logs re-query with their own
/// filters, so the hub never has to know which entries each viewer is allowed to see or is filtering for.
/// </summary>
/// <param name="LastSeenAt">The server's new last-seen time, by the API's clock.</param>
public sealed record LogsReceivedEvent(int ServerId, string ServerName, int Count, LogSeverity MaxSeverity, DateTimeOffset LastSeenAt);

/// <summary>Metric samples were stored. They are small, so they travel whole and charts can append them directly.</summary>
/// <param name="LastSeenAt">The server's new last-seen time, by the API's clock.</param>
public sealed record MetricsReceivedEvent(int ServerId, string ServerName, IReadOnlyList<MetricSample> Samples, DateTimeOffset LastSeenAt);
