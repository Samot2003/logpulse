using LogPulse.Core.Contracts;
using LogPulse.Dashboard.Api;
using LogPulse.Dashboard.Auth;
using LogPulse.Dashboard.Live;
using LogPulse.Dashboard.Options;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Options;

namespace LogPulse.Dashboard.Components;

/// <summary>
/// Base of the interactive pages: subscribes to the live feed once the page is interactive, runs the handlers on the
/// page's dispatcher, reloads whenever the feed becomes live (events sent before it connected, or while it was
/// disconnected, are lost), shows API failures as a notice and sends an expired session to the login page.
/// </summary>
public abstract class LivePageBase : ComponentBase, IDisposable
{
    private LiveStatus _lastStatus;
    private bool _subscribed;

    [Inject]
    protected ILiveFeed Feed { get; set; } = default!;

    [Inject]
    protected NavigationManager Navigation { get; set; } = default!;

    [Inject]
    protected TimeProvider Time { get; set; } = default!;

    [Inject]
    protected IOptions<DashboardOptions> Options { get; set; } = default!;

    /// <summary>The last API failure, shown at the top of the page; cleared by the next successful load.</summary>
    protected string? Error { get; set; }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing && _subscribed)
        {
            Feed.LogsReceived -= HandleLogs;
            Feed.MetricsReceived -= HandleMetrics;
            Feed.StatusChanged -= HandleStatus;
        }
    }

    protected override void OnAfterRender(bool firstRender)
    {
        // Not during prerendering: the live connection belongs to the interactive circuit.
        if (!firstRender)
        {
            return;
        }

        Feed.LogsReceived += HandleLogs;
        Feed.MetricsReceived += HandleMetrics;
        Feed.StatusChanged += HandleStatus;
        _subscribed = true;
        _lastStatus = Feed.Status;
        Feed.Start();
    }

    /// <summary>
    /// Runs a load. Returns false if it failed (the error is shown) or the session expired (redirected).
    /// Only the page's main load clears a shown error: a side load (a list of names, a refresh of one section)
    /// succeeding says nothing about what failed. A load superseded by a newer one (<paramref name="isCurrent"/>
    /// false when it ends) leaves the error alone either way.
    /// </summary>
    protected async Task<bool> TryLoadAsync(Func<Task> load, bool clearsError = true, Func<bool>? isCurrent = null)
    {
        ArgumentNullException.ThrowIfNull(load);
        try
        {
            await load();
            if (clearsError && (isCurrent?.Invoke() ?? true))
            {
                Error = null;
            }

            return true;
        }
        catch (SessionExpiredException)
        {
            GoToLogin();
            return false;
        }
        catch (Exception ex) when (ApiErrors.IsApiFailure(ex))
        {
            if (isCurrent?.Invoke() ?? true)
            {
                Error = ApiErrors.Describe(ex);
            }

            return false;
        }
    }

    protected virtual Task OnLogsReceivedAsync(LogsReceivedEvent received) => Task.CompletedTask;

    protected virtual Task OnMetricsReceivedAsync(MetricsReceivedEvent received) => Task.CompletedTask;

    /// <summary>The live connection has just opened or come back: reload what may have been missed.</summary>
    protected virtual Task OnReconnectedAsync() => Task.CompletedTask;

    private void GoToLogin() =>
        Navigation.NavigateTo("login?expired=1&returnUrl=" + Uri.EscapeDataString("/" + Navigation.ToBaseRelativePath(Navigation.Uri)), forceLoad: true);

    /// <summary>
    /// Runs work from a timer or a live event on the page's dispatcher. Nobody awaits it, so an unexpected exception
    /// is handed to Blazor, which logs it and shows the error UI, instead of disappearing.
    /// </summary>
    protected void RunOnDispatcher(Func<Task> work) => _ = InvokeAsync(async () =>
    {
        try
        {
            await work();
            StateHasChanged();
        }
        catch (Exception ex)
        {
            await DispatchExceptionAsync(ex);
        }
    });

    private void HandleLogs(LogsReceivedEvent received) => RunOnDispatcher(() => OnLogsReceivedAsync(received));

    private void HandleMetrics(MetricsReceivedEvent received) => RunOnDispatcher(() => OnMetricsReceivedAsync(received));

    // The status travels with the event: by the time the dispatcher runs this, the feed may have moved on
    // (Reconnecting, then Live again), and reading it then would hide the drop.
    private void HandleStatus(LiveStatus status) => RunOnDispatcher(async () =>
    {
        var wasLive = _lastStatus == LiveStatus.Live;
        _lastStatus = status;
        if (status == LiveStatus.SessionExpired)
        {
            GoToLogin();
        }
        else if (status == LiveStatus.Live && !wasLive)
        {
            await OnReconnectedAsync();
        }
    });
}
