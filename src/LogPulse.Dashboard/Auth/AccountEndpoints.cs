using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace LogPulse.Dashboard.Auth;

public static class AccountEndpoints
{
    public const string LogoutPath = "/account/logout";

    /// <summary>
    /// Logout is a POST with an antiforgery token (a link would let any site log users out), it revokes the
    /// refresh token in the API and then removes the cookie.
    /// </summary>
    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(LogoutPath, async (HttpContext http, IAntiforgery antiforgery, SessionStore sessions) =>
            {
                // Checked here: the antiforgery middleware only rejects requests for endpoints that bind form data.
                if (!await antiforgery.IsRequestValidAsync(http))
                {
                    return Results.BadRequest();
                }

                if (http.User.FindFirst(DashboardClaims.SessionId)?.Value is { } id)
                {
                    await sessions.EndAsync(id, http.RequestAborted);
                }

                await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                return Results.LocalRedirect("/login");
            });
        return endpoints;
    }
}
