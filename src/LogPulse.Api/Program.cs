using System.Net;
using System.Text.Json.Serialization;
using LogPulse.Api.Auth;
using LogPulse.Api.Background;
using LogPulse.Api.Controllers;
using LogPulse.Api.Infrastructure;
using LogPulse.Api.Ingest;
using LogPulse.Api.Live;
using LogPulse.Api.Options;
using LogPulse.Core.Contracts;
using LogPulse.Core.Models;
using LogPulse.Data;
using LogPulse.Data.Daos;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

// Options. EnsureValidConfiguration (below) validates them before the database is touched.
builder.Services.AddOptions<JwtOptions>().BindConfiguration(JwtOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<RateLimitingOptions>().BindConfiguration(RateLimitingOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<RetentionOptions>().BindConfiguration(RetentionOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<SeedOptions>().BindConfiguration(SeedOptions.SectionName);

// Data access: the API only sees the DAO interfaces.
builder.Services.AddSingleton<IDbConnectionFactory>(sp =>
    new SqlConnectionFactory(DatabaseStartup.GetConnectionString(sp.GetRequiredService<IConfiguration>())));
builder.Services.AddSingleton<DatabaseInitializer>();
builder.Services.AddSingleton<IServerDao, SqlServerDao>();
builder.Services.AddSingleton<ILogDao, SqlLogDao>();
builder.Services.AddSingleton<IMetricDao, SqlMetricDao>();
builder.Services.AddSingleton<IUserDao, SqlUserDao>();
builder.Services.AddSingleton<IAgentCredentialDao, SqlAgentCredentialDao>();
builder.Services.AddSingleton<IRefreshTokenDao, SqlRefreshTokenDao>();

// Application services.
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();
builder.Services.AddSingleton<TokenService>();
builder.Services.AddSingleton<AuthService>();
builder.Services.AddSingleton<IngestService>();
builder.Services.AddSingleton<ViewerTracker>();
builder.Services.AddSingleton<IViewerPresence>(sp => sp.GetRequiredService<ViewerTracker>());
builder.Services.AddSingleton<ILiveUpdates, HubLiveUpdates>();
builder.Services.AddSingleton<RetentionService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RetentionService>());

// Authentication: short-lived JWTs signed with HMAC-SHA256.
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IOptions<JwtOptions>>((bearer, jwt) =>
    {
        bearer.MapInboundClaims = false;
        bearer.TokenValidationParameters = JwtSetup.ValidationParameters(jwt.Value);
    });

// Authorization: every endpoint requires an authenticated caller unless it opts out with [AllowAnonymous].
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
    .AddPolicy(Policies.Ingest, p => p.RequireRole(Roles.Agent))
    .AddPolicy(Policies.Read, p => p.RequireRole(Roles.Viewer, Roles.Admin))
    .AddPolicy(Policies.Admin, p => p.RequireRole(Roles.Admin));

// Rate limits: auth per client IP (brute force), ingestion per agent and queries per user.
builder.Services.AddLogPulseRateLimits();

// The dashboard calls /api/auth on behalf of its users and forwards their IP in X-Forwarded-For, so the auth rate
// limit counts each user instead of the dashboard as a whole. The header is only trusted from known proxies:
// loopback by default (the dashboard on the same machine), others listed in ForwardedHeaders:KnownProxies.
builder.Services.Configure<ForwardedHeadersOptions>(forwarded =>
{
    forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
    foreach (var proxy in builder.Configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? [])
    {
        forwarded.KnownProxies.Add(IPAddress.Parse(proxy));
    }
});

// MVC with JSON (enums as strings) and MessagePack input for ingestion only. Both enforce the batch limits while
// reading. Responses are always JSON: only agents send MessagePack, and only to save bandwidth on ingestion.
builder.Services
    .AddControllers(mvc => mvc.InputFormatters.Add(new MessagePackInputFormatter(IngestSerialization.MessagePackOptions)))
    .AddJsonOptions(json =>
    {
        json.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
        IngestSerialization.AddBatchLimits(json.JsonSerializerOptions);
    });

// Live updates for the dashboard. Its server connects with each user's JWT in the Authorization header.
builder.Services.AddSignalR();

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<BadRequestExceptionHandler>();
builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database");

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(swagger =>
{
    swagger.SwaggerDoc("v1", new OpenApiInfo { Title = "LogPulse API", Version = "v1" });
    var bearer = new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" },
    };
    swagger.AddSecurityDefinition("Bearer", bearer);
    swagger.AddSecurityRequirement(new OpenApiSecurityRequirement { [bearer] = [] });
});

var app = builder.Build();

app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseRouting();
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

app.MapControllers();

// A connection outlives its access token, so it is closed when the token expires and the client reconnects with
// a fresh one: a revoked or expired user stops receiving data within one token lifetime.
app.MapHub<LiveHub>(LiveHubRoute.Path, hub => hub.CloseOnAuthenticationExpiration = true);
app.MapHealthChecks("/health").AllowAnonymous();

app.EnsureValidConfiguration();
await app.InitializeDatabaseAsync();
await app.RunAsync();

/// <summary>Entry point; public so integration tests can host the API with WebApplicationFactory.</summary>
public partial class Program;
