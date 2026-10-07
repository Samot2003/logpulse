using LogPulse.Api.Infrastructure;
using LogPulse.Api.Ingest;
using LogPulse.Core.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace LogPulse.Api.Live;

/// <summary>
/// Pushes new data to dashboard viewers. It has no methods clients can call: viewers only listen, and every
/// read still goes through the query endpoints with their filters, paging and rate limits.
/// </summary>
[Authorize(Policy = Policies.Read)]
public sealed class LiveHub(ViewerTracker viewers) : Hub<ILiveClient>
{
    public override Task OnConnectedAsync()
    {
        viewers.Connected();
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        viewers.Disconnected();
        return base.OnDisconnectedAsync(exception);
    }
}
