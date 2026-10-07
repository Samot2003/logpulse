using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LogPulse.Core.Models;
using LogPulse.Core.Queries;
using LogPulse.Dashboard.Auth;

namespace LogPulse.Dashboard.Api;

/// <summary>The API's query endpoints, called with the current user's token.</summary>
public interface ILogPulseApi
{
    Task<IReadOnlyList<Server>> GetServersAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MetricSample>> GetLatestMetricsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MetricSample>> GetMetricsAsync(int serverId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default);

    Task<PagedResult<LogEntry>> GetLogsAsync(LogQuery query, CancellationToken cancellationToken = default);
}

/// <summary>
/// Typed HTTP client, created per page in the user's circuit. On a 401 it renews the token once and retries:
/// the token can expire between the check and the request, or the API can refuse it early.
/// </summary>
public sealed class LogPulseApiClient(HttpClient http, CurrentSession session, SessionStore sessions) : ILogPulseApi
{
    /// <summary>The API's JSON: camelCase with enums as strings.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public async Task<IReadOnlyList<Server>> GetServersAsync(CancellationToken cancellationToken = default) =>
        await GetAsync<List<Server>>("api/servers", cancellationToken);

    public async Task<IReadOnlyList<MetricSample>> GetLatestMetricsAsync(CancellationToken cancellationToken = default) =>
        await GetAsync<List<MetricSample>>("api/metrics/latest", cancellationToken);

    public async Task<IReadOnlyList<MetricSample>> GetMetricsAsync(int serverId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default) =>
        await GetAsync<List<MetricSample>>(
            string.Create(CultureInfo.InvariantCulture, $"api/metrics/{serverId}?from={Escape(from)}&to={Escape(to)}"), cancellationToken);

    public Task<PagedResult<LogEntry>> GetLogsAsync(LogQuery query, CancellationToken cancellationToken = default) =>
        GetAsync<PagedResult<LogEntry>>(LogsPath(query), cancellationToken);

    /// <summary>The /api/logs URL for a query; only the filters that are set are sent.</summary>
    public static string LogsPath(LogQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var path = new StringBuilder("api/logs?page=").Append(query.Page.ToString(CultureInfo.InvariantCulture))
            .Append("&pageSize=").Append(query.PageSize.ToString(CultureInfo.InvariantCulture));
        if (query.ServerId is { } serverId)
        {
            path.Append("&serverId=").Append(serverId.ToString(CultureInfo.InvariantCulture));
        }

        if (query.MinSeverity is { } severity)
        {
            path.Append("&minSeverity=").Append(severity);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            path.Append("&search=").Append(Uri.EscapeDataString(query.Search));
        }

        if (query.From is { } from)
        {
            path.Append("&from=").Append(Escape(from));
        }

        if (query.To is { } to)
        {
            path.Append("&to=").Append(Escape(to));
        }

        return path.ToString();
    }

    private static string Escape(DateTimeOffset value) => Uri.EscapeDataString(value.ToString("O", CultureInfo.InvariantCulture));

    private async Task<T> GetAsync<T>(string path, CancellationToken cancellationToken)
    {
        var sessionId = await session.GetIdAsync();
        var token = await sessions.GetAccessTokenAsync(sessionId, rejectedToken: null, cancellationToken);
        var response = await SendAsync(path, token, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            response.Dispose();
            token = await sessions.GetAccessTokenAsync(sessionId, rejectedToken: token, cancellationToken);
            response = await SendAsync(path, token, cancellationToken);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                // A freshly renewed token refused as well: the user was removed or the API's keys changed.
                await sessions.EndAsync(sessionId, cancellationToken);
                throw new SessionExpiredException();
            }

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken)
                ?? throw new HttpRequestException("The API returned an empty response.");
        }
    }

    private async Task<HttpResponseMessage> SendAsync(string path, string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await http.SendAsync(request, cancellationToken);
    }
}
