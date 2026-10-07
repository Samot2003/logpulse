using System.Net;

namespace LogPulse.Dashboard.Api;

public static class ApiErrors
{
    /// <summary>Whether a page should show the error instead of failing the circuit.</summary>
    public static bool IsApiFailure(Exception exception) => exception is HttpRequestException or TaskCanceledException;

    /// <summary>A message for the user; details stay in the server logs.</summary>
    public static string Describe(Exception exception) => exception switch
    {
        HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } => "Too many requests to the API. Wait a moment and try again.",
        HttpRequestException { StatusCode: null } => "The API is not reachable. Check that it is running.",
        HttpRequestException { StatusCode: { } status } => $"The API returned an error ({(int)status}).",
        TaskCanceledException => "The API took too long to answer.",
        _ => "Something went wrong while loading the data.",
    };
}
