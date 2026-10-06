using System.Text;
using System.Threading.Channels;
using LogPulse.Core.Contracts;
using MessagePack;
using MessagePack.Resolvers;

namespace LogPulse.Agent.Shipping;

public static class AgentSerialization
{
    public const string MessagePackMediaType = "application/x-msgpack";

    /// <summary>Contractless: property names as string keys, which is what the API's MessagePack reader expects.</summary>
    public static MessagePackSerializerOptions MessagePack { get; } =
        MessagePackSerializerOptions.Standard.WithResolver(ContractlessStandardResolver.Instance);
}

/// <summary>Takes items from the buffers in batches the API accepts: by item count and, for logs, by size.</summary>
public static class BatchBuilder
{
    /// <summary>
    /// Upper bound for everything in a serialized entry except its strings: map header, the five keys, timestamp,
    /// severity and string headers (about 80 bytes in practice).
    /// </summary>
    private const int EntryOverheadBytes = 128;

    private const int BatchOverheadBytes = 32;

    /// <summary>An upper bound of the MessagePack size of the entry, without serializing it.</summary>
    public static int EstimateBytes(IngestLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return EntryOverheadBytes
            + Encoding.UTF8.GetByteCount(entry.Source)
            + Encoding.UTF8.GetByteCount(entry.Message)
            + (entry.Exception is null ? 0 : Encoding.UTF8.GetByteCount(entry.Exception));
    }

    /// <summary>
    /// Takes buffered lines while the batch stays within <paramref name="maxItems"/> and (estimated)
    /// <paramref name="maxBytes"/>. Takes at least one line if any is waiting: fields are already truncated, so
    /// a single entry is always far below the smallest allowed <paramref name="maxBytes"/>.
    /// </summary>
    public static List<PendingLog> TakeLogs(ChannelReader<PendingLog> reader, int maxItems, int maxBytes)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var batch = new List<PendingLog>();
        var bytes = BatchOverheadBytes;
        while (batch.Count < maxItems && reader.TryPeek(out var next))
        {
            var size = EstimateBytes(next.Entry);
            if (batch.Count > 0 && bytes + size > maxBytes)
            {
                break;
            }

            reader.TryRead(out _);
            batch.Add(next);
            bytes += size;
        }

        return batch;
    }

    /// <summary>Metric samples are small and fixed-size: only the item count matters.</summary>
    public static List<IngestMetricSample> TakeMetrics(ChannelReader<IngestMetricSample> reader, int maxItems)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var batch = new List<IngestMetricSample>();
        while (batch.Count < maxItems && reader.TryRead(out var sample))
        {
            batch.Add(sample);
        }

        return batch;
    }
}
