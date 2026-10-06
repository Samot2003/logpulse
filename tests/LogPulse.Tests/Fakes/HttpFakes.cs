using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LogPulse.Core.Contracts;

namespace LogPulse.Tests.Fakes;

public sealed record RecordedRequest(string Path, string? BearerToken, string? ContentType, ReadOnlyMemory<byte> Body);

/// <summary>Answers HTTP requests with a function and records what it received.</summary>
public sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    private readonly List<RecordedRequest> _requests = [];

    /// <summary>Awaited before answering; lets a test hold requests in flight to make them overlap.</summary>
    public Func<Task>? BeforeResponse { get; set; }

    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_requests)
            {
                return [.. _requests];
            }
        }
    }

    public int CountTo(string path) => Requests.Count(r => r.Path == path);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        lock (_requests)
        {
            _requests.Add(new RecordedRequest(
                request.RequestUri!.AbsolutePath,
                request.Headers.Authorization?.Parameter,
                request.Content?.Headers.ContentType?.MediaType,
                body));
        }

        if (BeforeResponse is { } wait)
        {
            await wait();
        }

        return respond(request);
    }

    public static HttpResponseMessage Json<T>(T value, HttpStatusCode status = HttpStatusCode.OK, DateTimeOffset? date = null)
    {
        var response = new HttpResponseMessage(status) { Content = JsonContent.Create(value, options: new JsonSerializerOptions(JsonSerializerDefaults.Web)) };
        response.Headers.Date = date;
        return response;
    }

    public static HttpResponseMessage Tokens(string access, string refresh, DateTimeOffset serverNow, TimeSpan accessLifetime) =>
        Json(new TokenResponse(access, serverNow + accessLifetime, refresh, serverNow.AddDays(7)), date: serverNow);
}

/// <summary>Every client goes to the same handler.</summary>
public sealed class StubHttpClientFactory(HttpMessageHandler handler, Uri? baseAddress = null) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) =>
        new(handler, disposeHandler: false) { BaseAddress = baseAddress ?? new Uri("http://api.test/") };
}

/// <summary>Runs only on Windows (skipped elsewhere).</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Windows only.";
        }
    }
}

/// <summary>Runs only on Linux (skipped elsewhere).</summary>
public sealed class LinuxFactAttribute : FactAttribute
{
    public LinuxFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = "Linux only.";
        }
    }
}
