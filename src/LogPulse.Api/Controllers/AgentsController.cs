using System.ComponentModel.DataAnnotations;
using LogPulse.Api.Auth;
using LogPulse.Api.Infrastructure;
using LogPulse.Core.Contracts;
using LogPulse.Core.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LogPulse.Api.Controllers;

[ApiController]
[Route("api/agents")]
[Authorize(Policy = Policies.Admin)]
[Produces("application/json")]
public sealed class AgentsController(AuthService auth) : ControllerBase
{
    /// <summary>
    /// Creates an agent credential, or rotates the key of an existing one. Rotating ends every session opened
    /// with the old key. The new key is shown only once.
    /// </summary>
    [HttpPost]
    [ProducesResponseType<CreateAgentResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create(CreateAgentRequest request, CancellationToken cancellationToken)
    {
        var created = await auth.CreateAgentAsync(request.ServerName, cancellationToken);
        return StatusCode(StatusCodes.Status201Created, created);
    }

    /// <summary>
    /// Revokes the agent's key and ends its sessions (issued access tokens expire within minutes). Revoking an
    /// already revoked agent succeeds again (204); only a server that never had a credential is a 404.
    /// </summary>
    [HttpDelete("{serverName}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Revoke(
        [StringLength(FieldLimits.ServerName)][RegularExpression(FieldLimits.ServerNamePattern)] string serverName,
        CancellationToken cancellationToken) =>
        await auth.RevokeAgentAsync(serverName, cancellationToken)
            ? NoContent()
            : Problem(statusCode: StatusCodes.Status404NotFound, title: "Agent not found");
}
