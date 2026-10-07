using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;

namespace LogPulse.Dashboard.Auth;

public static class DashboardClaims
{
    /// <summary>The id of the server-side session; the only link between the browser cookie and the user's tokens.</summary>
    public const string SessionId = "lp_session";

    public static ClaimsPrincipal Principal(DashboardSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, session.UserName), new Claim(SessionId, session.Id)],
            CookieAuthenticationDefaults.AuthenticationScheme));
    }
}

/// <summary>
/// Rejects a cookie whose session is gone (logout in another tab, refused refresh, dashboard restart), so the user
/// is sent to the login page instead of seeing pages that cannot load any data.
/// </summary>
public sealed class SessionCookieEvents(SessionStore sessions) : CookieAuthenticationEvents
{
    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Principal?.FindFirst(DashboardClaims.SessionId)?.Value is { } id && sessions.Find(id) is not null)
        {
            return;
        }

        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }
}

/// <summary>The session of the user of the current request or Blazor circuit.</summary>
public sealed class CurrentSession(AuthenticationStateProvider authenticationState)
{
    /// <exception cref="SessionExpiredException">The user is not logged in.</exception>
    public async Task<string> GetIdAsync()
    {
        var state = await authenticationState.GetAuthenticationStateAsync();
        return state.User.FindFirst(DashboardClaims.SessionId)?.Value ?? throw new SessionExpiredException();
    }
}

public static class LocalUrl
{
    /// <summary>
    /// The URL if it is a path on this site, otherwise the home page. Stops the login page's return URL from sending
    /// users to another site ("//evil.example", "/\evil.example", "https://evil.example").
    /// </summary>
    public static string OrHome(string? url) =>
        url is ['/', ..] && !url.StartsWith("//", StringComparison.Ordinal) && !url.StartsWith("/\\", StringComparison.Ordinal)
            && !url.Any(char.IsControl)
            ? url
            : "/";
}
