using LogPulse.Api.Ingest;
using LogPulse.Core.Contracts;
using LogPulse.Core.Models;
using Microsoft.AspNetCore.SignalR;

namespace LogPulse.Api.Live;

/// <summary>Tells viewers about data that was just stored.</summary>
public interface ILiveUpdates
{
    Task LogsStoredAsync(Server server, IReadOnlyList<LogEntry> entries);

    Task MetricsStoredAsync(Server server, IReadOnlyList<MetricSample> samples);
}

/// <summary>
/// Broadcasts through the live hub. The batch is already stored when this runs, so a failure here must never fail
/// the ingestion request: the agent would resend a batch the database already has. Failures are logged instead,
/// and a slow viewer connection can hold the request for at most <see cref="SendTimeout"/>.
/// </summary>
public sealed partial class HubLiveUpdates(
    IHubContext<LiveHub, ILiveClient> hub, IViewerPresence viewers, ILogger<HubLiveUpdates> logger) : ILiveUpdates
{
    public static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(2);

    private readonly ILogger _logger = logger;

    public Task LogsStoredAsync(Server server, IReadOnlyList<LogEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(entries);
        return entries.Count == 0
            ? Task.CompletedTask
            : SendAsync(() => hub.Clients.All.LogsReceived(
                new LogsReceivedEvent(server.Id, server.Name, entries.Count, entries.Max(e => e.Severity), server.LastSeenAt)));
    }

    public Task MetricsStoredAsync(Server server, IReadOnlyList<MetricSample> samples)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(samples);
        return samples.Count == 0
            ? Task.CompletedTask
            : SendAsync(() => hub.Clients.All.MetricsReceived(new MetricsReceivedEvent(server.Id, server.Name, samples, server.LastSeenAt)));
    }

    private async Task SendAsync(Func<Task> send)
    {
        // Nobody to tell: skip the serialization.
        if (!viewers.AnyViewers)
        {
            return;
        }

        try
        {
            await send().WaitAsync(SendTimeout);
        }
        catch (Exception ex)
        {
            LogSendFailed(ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not notify the dashboard viewers; the data is stored and they will see it on their next query.")]
    private partial void LogSendFailed(Exception exception);
}
