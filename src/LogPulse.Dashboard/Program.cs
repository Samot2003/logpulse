using LogPulse.Dashboard.Api;
using LogPulse.Dashboard.Auth;
using LogPulse.Dashboard.Components;
using LogPulse.Dashboard.Live;
using LogPulse.Dashboard.Options;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<DashboardOptions>().BindConfiguration(DashboardOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<LiveHubConnectionOptions>();

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddHealthChecks();

// Sessions: the cookie only carries the id of a server-side session that holds the user's tokens.
builder.Services.AddMemoryCache();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<SessionStore>();
builder.Services.AddSingleton<SessionCookieEvents>();
builder.Services.AddScoped<CurrentSession>();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(cookie =>
    {
        cookie.LoginPath = "/login";
        cookie.AccessDeniedPath = "/login";
        cookie.EventsType = typeof(SessionCookieEvents);
        cookie.Cookie.Name = "LogPulse.Session";
        cookie.Cookie.HttpOnly = true;
        cookie.Cookie.SameSite = SameSiteMode.Strict;
        cookie.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;

        // A browser-session cookie, renewed while the user keeps loading pages. How long a user stays logged in is
        // really decided by the server-side session, which ends when the API stops renewing its refresh token.
        cookie.ExpireTimeSpan = TimeSpan.FromHours(12);
        cookie.SlidingExpiration = true;
    });
builder.Services.AddAuthorization();

// The API: auth endpoints (no retries, single-use refresh tokens), queries with the user's token, and the live hub.
builder.Services.AddHttpClient(AuthApiClient.HttpClientName, ConfigureApiClient)
    .ConfigureHttpClient(http => http.Timeout = TimeSpan.FromSeconds(15))
    .RedactLoggedHeaders(["Authorization"]);
builder.Services.AddSingleton<IAuthApi, AuthApiClient>();
builder.Services.AddHttpClient<ILogPulseApi, LogPulseApiClient>(ConfigureApiClient)
    .ConfigureHttpClient(http => http.Timeout = TimeSpan.FromSeconds(30))
    .RedactLoggedHeaders(["Authorization"]);
builder.Services.AddScoped<ILiveFeed, LiveFeed>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error", createScopeForErrors: true);
    app.UseHsts();
}

app.Use(async (context, next) =>
{
    // Blazor Server needs no inline scripts and no eval. Inline styles are used for the usage bars and by Blazor's
    // reconnection dialog; the WebSocket to this same origin is covered by 'self'.
    var headers = context.Response.Headers;
    headers.ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; "
        + "frame-ancestors 'none'; base-uri 'self'; form-action 'self'; object-src 'none'";
    headers.XContentTypeOptions = "nosniff";
    headers.XFrameOptions = "DENY";
    headers["Referrer-Policy"] = "no-referrer";
    await next();
});

app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.MapAccountEndpoints();
app.MapHealthChecks("/health");

app.EnsureValidDashboardConfiguration();
await app.RunAsync();

static void ConfigureApiClient(IServiceProvider services, HttpClient http)
{
    // ApiBaseUrl is [Required] and validated on start.
    http.BaseAddress = ApiAddress.Normalize(services.GetRequiredService<IOptions<DashboardOptions>>().Value.ApiBaseUrl!);
}
