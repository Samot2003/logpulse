namespace LogPulse.Dashboard.Api;

public static class ApiAddress
{
    /// <summary>
    /// Ensures a trailing slash, so relative paths keep the base path: "https://host/logpulse" + "api/servers"
    /// would otherwise become "https://host/api/servers".
    /// </summary>
    public static Uri Normalize(Uri baseUrl)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return baseUrl.AbsoluteUri.EndsWith('/') ? baseUrl : new Uri(baseUrl.AbsoluteUri + "/");
    }
}
