using LogPulse.Api.Infrastructure;
using LogPulse.Api.Ingest;
using LogPulse.Core.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LogPulse.Api.Controllers;

[ApiController]
[Route("api/ingest")]
[Authorize(Policy = Policies.Ingest)]
[Consumes("application/json", MessagePackInputFormatter.MediaType)]
[Produces("application/json")]
[RequestSizeLimit(IngestLimits.MaxRequestBytes)]
public sealed class IngestController(IngestService ingest, IViewerPresence viewers) : ControllerBase
{
    /// <summary>Stores a batch of log entries for the agent's own server (JSON or MessagePack).</summary>
    [HttpPost("logs")]
    [ProducesResponseType<IngestResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Logs(IngestLogBatch batch, CancellationToken cancellationToken)
    {
        if (ServerName is not { } serverName)
        {
            return Forbid();
        }

        return Ok(new IngestResult(await ingest.IngestLogsAsync(serverName, batch, cancellationToken), viewers.AnyViewers));
    }

    /// <summary>Stores a batch of metric samples for the agent's own server (JSON or MessagePack).</summary>
    [HttpPost("metrics")]
    [ProducesResponseType<IngestResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Metrics(IngestMetricBatch batch, CancellationToken cancellationToken)
    {
        if (ServerName is not { } serverName)
        {
            return Forbid();
        }

        return Ok(new IngestResult(await ingest.IngestMetricsAsync(serverName, batch, cancellationToken), viewers.AnyViewers));
    }

    // The server is the one the agent authenticated as; the payload has no way to name another one.
    private string? ServerName => User.Identity?.Name is { Length: > 0 } name ? name : null;
}
