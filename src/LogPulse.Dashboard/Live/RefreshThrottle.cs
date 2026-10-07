namespace LogPulse.Dashboard.Live;

/// <summary>
/// Runs a refresh at most once per interval. Requests that arrive while one is waiting merge into it, so a burst
/// of live events (several agents sending every few seconds) costs one query instead of one per event.
/// Meant for one component: call <see cref="Request"/> from its dispatcher.
/// </summary>
public sealed class RefreshThrottle(TimeSpan interval, TimeProvider time, Func<Task> refresh) : IDisposable
{
    private readonly CancellationTokenSource _disposed = new();
    private DateTimeOffset _lastRun = DateTimeOffset.MinValue;
    private bool _pending;

    public void Request()
    {
        if (_pending || _disposed.IsCancellationRequested)
        {
            return;
        }

        _pending = true;
        _ = RunAsync();
    }

    public void Dispose()
    {
        _disposed.Cancel();
        _disposed.Dispose();
    }

    private async Task RunAsync()
    {
        var token = _disposed.Token;
        var wait = _lastRun + interval - time.GetUtcNow();
        try
        {
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, time, token);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // Cleared before refreshing: events that arrive during the refresh schedule the next one.
        _pending = false;
        _lastRun = time.GetUtcNow();
        await refresh();
    }
}
