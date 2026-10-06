using System.Threading.Channels;
using LogPulse.Agent.Logs;
using LogPulse.Agent.Options;
using LogPulse.Core.Contracts;
using Microsoft.Extensions.Options;

namespace LogPulse.Agent.Shipping;

/// <summary>A log line waiting to be sent, and the file position to remember once the API has stored it.</summary>
public sealed record PendingLog(IngestLogEntry Entry, LogPosition Position);

/// <summary>
/// Bounded queues between the producers (log tailer, metrics sampler) and the sender, so a slow or unreachable
/// API never makes the agent's memory grow without limit.
/// </summary>
public sealed class AgentBuffers
{
    public AgentBuffers(IOptions<AgentOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var capacity = options.Value.BufferCapacity;

        // Full: the tailer waits. Nothing is lost, the lines stay in the file until there is room again.
        Logs = Channel.CreateBounded<PendingLog>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });

        // Full: the oldest sample is dropped. After a long outage the recent samples matter most.
        Metrics = Channel.CreateBounded<IngestMetricSample>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });
    }

    public Channel<PendingLog> Logs { get; }

    public Channel<IngestMetricSample> Metrics { get; }
}
