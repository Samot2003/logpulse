using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using LogPulse.Agent.Auth;
using LogPulse.Core.Contracts;
using MessagePack;
using Polly;

namespace LogPulse.Agent.Shipping;

public enum SendOutcome
{
    /// <summary>The API stored the batch.</summary>
    Accepted,

    /// <summary>A temporary problem (network, API down, credentials refused): keep the batch and try again later.</summary>
    RetryLater,

    /// <summary>The API refused this data for good (400, 422): sending it again would fail again.</summary>
    Rejected,

    /// <summary>The request was too large for the API or a proxy in front of it (413): send it in smaller parts.</summary>
    TooLarge,
}

public readonly record struct SendResult(SendOutcome Outcome, bool ViewersOnline, string Detail)
{
    public static SendResult Accepted(bool viewersOnline) => new(SendOutcome.Accepted, viewersOnline, string.Empty);

    public static SendResult RetryLater(string reason) => new(SendOutcome.RetryLater, true, reason);

    public static SendResult Rejected(string reason) => new(SendOutcome.Rejected, true, reason);

    public static SendResult TooLarge(string reason) => new(SendOutcome.TooLarge, true, reason);
}

public interface IIngestClient
{
    Task<SendResult> SendLogsAsync(IngestLogBatch batch, CancellationToken cancellationToken);

    Task<SendResult> SendMetricsAsync(IngestMetricBatch batch, CancellationToken cancellationToken);
}

/// <summary>
/// Posts batches to the API in MessagePack. The HTTP client behind it adds the bearer token and retries transient
/// failures with backoff (see <see cref="AgentServiceCollectionExtensions"/>).
/// </summary>
public sealed class IngestClient(IHttpClientFactory httpClients) : IIngestClient
{
    public const string HttpClientName = "LogPulse.Ingest";

    private static readonly Uri LogsEndpoint = new("api/ingest/logs", UriKind.Relative);
    private static readonly Uri MetricsEndpoint = new("api/ingest/metrics", UriKind.Relative);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The API's <see cref="IngestResult"/>, read leniently: a missing flag means "keep the full rate".</summary>
    private sealed record IngestResponse(int Accepted, bool? ViewersOnline);

    public Task<SendResult> SendLogsAsync(IngestLogBatch batch, CancellationToken cancellationToken) =>
        SendAsync(LogsEndpoint, batch, cancellationToken);

    public Task<SendResult> SendMetricsAsync(IngestMetricBatch batch, CancellationToken cancellationToken) =>
        SendAsync(MetricsEndpoint, batch, cancellationToken);

    private async Task<SendResult> SendAsync<TBatch>(Uri endpoint, TBatch batch, CancellationToken cancellationToken)
    {
        using var content = new ByteArrayContent(MessagePackSerializer.Serialize(batch, AgentSerialization.MessagePack, cancellationToken));
        content.Headers.ContentType = new MediaTypeHeaderValue(AgentSerialization.MessagePackMediaType);

        try
        {
            using var response = await httpClients.CreateClient(HttpClientName).PostAsync(endpoint, content, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                // Something that is not the API's JSON (e.g. a proxy's HTML page) throws and means "retry later".
                var result = await response.Content.ReadFromJsonAsync<IngestResponse>(Json, cancellationToken)
                    ?? throw new JsonException("Empty ingestion response.");
                return SendResult.Accepted(result.ViewersOnline ?? true);
            }

            // Only a payload the API cannot accept is dropped. Anything else (401 with a fresh token, 403, 404,
            // 415, 429, 5xx) points at the API, a proxy or the configuration, and the data is kept until fixed.
            var status = (int)response.StatusCode;
            return status switch
            {
                400 or 422 => SendResult.Rejected($"HTTP {status}"),
                413 => SendResult.TooLarge($"HTTP {status}"),
                _ => SendResult.RetryLater($"HTTP {status}"),
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or ExecutionRejectedException or AgentAuthenticationException or JsonException
                                   || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // Network errors, timeouts and an open circuit breaker (after the retries of the resilience handler),
            // refused credentials, or a response that is not the API's.
            return SendResult.RetryLater(ex.Message);
        }
    }
}
