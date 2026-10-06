namespace LogPulse.Agent.Shipping;

/// <summary>Whether anyone is watching the dashboard, as reported by the API in every ingestion response.</summary>
public sealed partial class ViewerActivity(ILogger<ViewerActivity> logger)
{
    // Until the API says otherwise, assume someone is watching and sample at full rate.
    private volatile bool _viewersOnline = true;

    public bool ViewersOnline => _viewersOnline;

    public void Report(bool viewersOnline)
    {
        if (_viewersOnline == viewersOnline)
        {
            return;
        }

        _viewersOnline = viewersOnline;
        LogViewersChanged(logger, viewersOnline);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Dashboard viewers online: {ViewersOnline}; adjusting the metrics rate")]
    private static partial void LogViewersChanged(ILogger logger, bool viewersOnline);
}
