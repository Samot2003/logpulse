using LogPulse.Core.Contracts;
using LogPulse.Dashboard.Api;
using LogPulse.Dashboard.Auth;
using LogPulse.Dashboard.Options;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Options;

namespace LogPulse.Dashboard.Live;

public enum LiveStatus
{
    Connecting,
    Live,
    Reconnecting,
    Offline,
    SessionExpired,
}

/// <summary>New data pushed by the API, for the pages of one Blazor circuit.</summary>
public interface ILiveFeed
{
    LiveStatus Status { get; }

    /// <summary>Raised on a background thread: handlers must switch to the component's dispatcher.</summary>
    event Action<LogsReceivedEvent>? LogsReceived;

    /// <summary>Raised on a background thread: handlers must switch to the component's dispatcher.</summary>
    event Action<MetricsReceivedEvent>? MetricsReceived;

    /// <summary>Raised on a background thread with the new <see cref="Status"/>.</summary>
    event Action<LiveStatus>? StatusChanged;

    /// <summary>Opens the connection if it is not open yet. Returns at once; connecting continues in the background.</summary>
    void Start();
}

/// <summary>Extra settings for the hub connection; the integration tests use it to reach an in-memory API.</summary>
public sealed class LiveHubConnectionOptions
{
    public Action<HttpConnectionOptions>? ConfigureHttp { get; set; }
}

/// <summary>
/// One SignalR connection to the API's live hub per Blazor circuit (per browser tab), with the user's own token.
/// While it is open, the API counts the tab as a viewer and agents sample at full speed. It reconnects on its own:
/// automatically after a dropped connection, and with a retry loop after a failed start or a close by the server
/// (the API closes connections when their access token expires).
/// </summary>
public sealed partial class LiveFeed : ILiveFeed, IAsyncDisposable
{
    public static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);

    private readonly IOptions<DashboardOptions> _options;
    private readonly IOptions<LiveHubConnectionOptions> _connectionOptions;
    private readonly CurrentSession _session;
    private readonly SessionStore _sessions;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stopping = new();
    private readonly CancellationToken _stoppingToken;
    private HubConnection? _connection;
    private string? _lastToken;
    private string? _rejectedToken;
    private bool _started;
    private int _status = (int)LiveStatus.Connecting;

    public LiveFeed(
        IOptions<DashboardOptions> options,
        IOptions<LiveHubConnectionOptions> connectionOptions,
        CurrentSession session,
        SessionStore sessions,
        TimeProvider time,
        ILogger<LiveFeed> logger)
    {
        _options = options;
        _connectionOptions = connectionOptions;
        _session = session;
        _sessions = sessions;
        _time = time;
        _logger = logger;
        _stoppingToken = _stopping.Token;
    }

    public event Action<LogsReceivedEvent>? LogsReceived;

    public event Action<MetricsReceivedEvent>? MetricsReceived;

    public event Action<LiveStatus>? StatusChanged;

    public LiveStatus Status => (LiveStatus)Volatile.Read(ref _status);

    public void Start()
    {
        // Components call this from the circuit's dispatcher, one at a time.
        if (_started)
        {
            return;
        }

        _started = true;
        _ = RunAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }

        _stopping.Dispose();
    }

    private async Task RunAsync()
    {
        string sessionId;
        try
        {
            sessionId = await _session.GetIdAsync();
        }
        catch (SessionExpiredException)
        {
            SetStatus(LiveStatus.SessionExpired);
            return;
        }

        // The circuit may have closed while the session was being read: then there is nothing to connect.
        if (_stoppingToken.IsCancellationRequested)
        {
            return;
        }

        // ApiBaseUrl is [Required] and validated on start.
        var hubUrl = new Uri(ApiAddress.Normalize(_options.Value.ApiBaseUrl!), LiveHubRoute.Path.TrimStart('/'));
        _connection = new HubConnectionBuilder()
            .WithUrl(hubUrl, http =>
            {
                // After a 401 the token that was refused is passed on, so the session renews it instead of retrying
                // with the same one until it expires locally (e.g. after the API's signing key changed).
                http.AccessTokenProvider = async () => _lastToken =
                    await _sessions.GetAccessTokenAsync(sessionId, Interlocked.Exchange(ref _rejectedToken, null), _stoppingToken);
                _connectionOptions.Value.ConfigureHttp?.Invoke(http);
            })
            .WithAutomaticReconnect(new RetryPolicy(this))
            .Build();

        _connection.On<LogsReceivedEvent>(nameof(ILiveClient.LogsReceived), received => LogsReceived?.Invoke(received));
        _connection.On<MetricsReceivedEvent>(nameof(ILiveClient.MetricsReceived), received => MetricsReceived?.Invoke(received));
        _connection.Reconnecting += _ =>
        {
            SetStatus(LiveStatus.Reconnecting);
            return Task.CompletedTask;
        };
        _connection.Reconnected += _ =>
        {
            SetStatus(LiveStatus.Live);
            return Task.CompletedTask;
        };
        _connection.Closed += error =>
        {
            if (!_stoppingToken.IsCancellationRequested)
            {
                _ = ConnectAsync(error, reconnecting: true);
            }

            return Task.CompletedTask;
        };

        await ConnectAsync(lastError: null, reconnecting: false);
    }

    /// <summary>Starts the connection, retrying with backoff until it opens, the session ends or the circuit closes.</summary>
    private async Task ConnectAsync(Exception? lastError, bool reconnecting)
    {
        var delay = TimeSpan.FromSeconds(1);
        while (!_stoppingToken.IsCancellationRequested)
        {
            if (IsSessionExpired(lastError))
            {
                SetStatus(LiveStatus.SessionExpired);
                return;
            }

            try
            {
                SetStatus(reconnecting ? LiveStatus.Reconnecting : LiveStatus.Connecting);
                await _connection!.StartAsync(_stoppingToken);
                SetStatus(LiveStatus.Live);
                return;
            }
            catch (OperationCanceledException) when (_stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
                NoteRejection(ex);

                if (!IsSessionExpired(ex))
                {
                    LogConnectFailed(ex, delay);
                    SetStatus(LiveStatus.Offline);
                }
            }

            if (!IsSessionExpired(lastError))
            {
                try
                {
                    await Task.Delay(delay, _time, _stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaxRetryDelay.Ticks));
            }
        }
    }

    private static bool IsSessionExpired(Exception? error) =>
        error is SessionExpiredException || error?.InnerException is SessionExpiredException;

    private void SetStatus(LiveStatus status)
    {
        // Written from SignalR's threads and the connect loop; only a real change is announced, once.
        if (Interlocked.Exchange(ref _status, (int)status) != (int)status)
        {
            StatusChanged?.Invoke(status);
        }
    }

    /// <summary>Remembers the token the API refused, so the next attempt asks the session for a renewed one.</summary>
    private void NoteRejection(Exception? error)
    {
        if (error is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized })
        {
            Volatile.Write(ref _rejectedToken, _lastToken);
        }
    }

    /// <summary>Keeps reconnecting after a dropped connection, every 30 s at most, unless the session has ended.</summary>
    private sealed class RetryPolicy(LiveFeed feed) : IRetryPolicy
    {
        public TimeSpan? NextRetryDelay(RetryContext retryContext)
        {
            if (IsSessionExpired(retryContext.RetryReason))
            {
                return null;
            }

            feed.NoteRejection(retryContext.RetryReason);
            return TimeSpan.FromSeconds(Math.Min(MaxRetryDelay.TotalSeconds, Math.Pow(2, Math.Min(retryContext.PreviousRetryCount, 5))));
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not connect to the API's live hub; retrying in {Delay}")]
    private partial void LogConnectFailed(Exception exception, TimeSpan delay);
}
