using System.Net;
using System.Net.Http.Headers;

namespace LogPulse.Agent.Auth;

/// <summary>Adds the agent's bearer token to every API request; on a 401 it renews the token once and retries.</summary>
public sealed class AuthTokenHandler(AgentTokenProvider tokens) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var token = await tokens.GetAccessTokenAsync(cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await base.SendAsync(request, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return response;
        }

        // The token stopped working before its expiry (e.g. the API restarted with a new signing key). Renew it
        // once; a second 401 goes back to the caller.
        response.Dispose();
        tokens.Invalidate(token);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokens.GetAccessTokenAsync(cancellationToken));
        return await base.SendAsync(request, cancellationToken);
    }
}
