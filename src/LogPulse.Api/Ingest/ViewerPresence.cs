namespace LogPulse.Api.Ingest;

/// <summary>Tells agents, in every ingestion response, whether anyone is watching the dashboard.</summary>
public interface IViewerPresence
{
    bool AnyViewers { get; }
}

/// <summary>
/// Used until the live dashboard tracks its connections: it reports that someone is always watching, so agents
/// keep sampling at full frequency.
/// </summary>
public sealed class AlwaysWatchedPresence : IViewerPresence
{
    public bool AnyViewers => true;
}
