using LogPulse.Api.Infrastructure;
using LogPulse.Core.Models;
using LogPulse.Core.Queries;
using LogPulse.Data.Daos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace LogPulse.Api.Controllers;

[ApiController]
[Route("api/servers")]
[Authorize(Policy = Policies.Read)]
[EnableRateLimiting(Policies.ReadRateLimit)]
[Produces("application/json")]
public sealed class ServersController(IServerDao servers) : ControllerBase
{
    /// <summary>Every server that has ever reported, ordered by name.</summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<Server>>(StatusCodes.Status200OK)]
    public async Task<IReadOnlyList<Server>> GetAll(CancellationToken cancellationToken) =>
        await servers.GetAllAsync(cancellationToken);
}

[ApiController]
[Route("api/logs")]
[Authorize(Policy = Policies.Read)]
[EnableRateLimiting(Policies.ReadRateLimit)]
[Produces("application/json")]
public sealed class LogsController(ILogDao logs) : ControllerBase
{
    /// <summary>Log entries, newest first, filtered and paged.</summary>
    [HttpGet]
    [ProducesResponseType<PagedResult<LogEntry>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<PagedResult<LogEntry>> Query([FromQuery] LogQueryParameters parameters, CancellationToken cancellationToken) =>
        await logs.QueryAsync(parameters.ToQuery(), cancellationToken);
}

[ApiController]
[Route("api/metrics")]
[Authorize(Policy = Policies.Read)]
[EnableRateLimiting(Policies.ReadRateLimit)]
[Produces("application/json")]
public sealed class MetricsController(IMetricDao metrics, IServerDao servers, TimeProvider time) : ControllerBase
{
    /// <summary>The latest sample of every server.</summary>
    [HttpGet("latest")]
    [ProducesResponseType<IReadOnlyList<MetricSample>>(StatusCodes.Status200OK)]
    public async Task<IReadOnlyList<MetricSample>> Latest(CancellationToken cancellationToken) =>
        await metrics.GetLatestPerServerAsync(cancellationToken);

    /// <summary>Samples of one server, oldest first. Defaults to the last hour; at most 24 hours.</summary>
    [HttpGet("{serverId:int}")]
    [ProducesResponseType<IReadOnlyList<MetricSample>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Range(int serverId, [FromQuery] MetricRangeParameters parameters, CancellationToken cancellationToken)
    {
        if (await servers.GetByIdAsync(serverId, cancellationToken) is null)
        {
            return Problem(statusCode: StatusCodes.Status404NotFound, title: "Server not found");
        }

        var (from, to) = parameters.Resolve(time.GetUtcNow());
        return Ok(await metrics.GetRangeAsync(serverId, from, to, cancellationToken: cancellationToken));
    }
}
