namespace LogPulse.Api.Ingest;

/// <summary>Tells agents, in every ingestion response, whether anyone is watching the dashboard.</summary>
public interface IViewerPresence
{
    bool AnyViewers { get; }
}

/// <summary>
/// Counts the open connections to the live hub. Each open dashboard page holds one, so a count above zero
/// means someone is watching and agents should sample at full frequency.
/// </summary>
public sealed class ViewerTracker : IViewerPresence
{
    private int _connections;

    public int Connections => Volatile.Read(ref _connections);

    public bool AnyViewers => Connections > 0;

    public void Connected() => Interlocked.Increment(ref _connections);

    public void Disconnected() => Interlocked.Decrement(ref _connections);
}
