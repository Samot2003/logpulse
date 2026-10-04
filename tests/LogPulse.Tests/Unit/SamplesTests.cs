using System.Text.Json;
using System.Text.Json.Serialization;
using LogPulse.Api.Infrastructure;
using LogPulse.Core.Contracts;
using MessagePack;

namespace LogPulse.Tests.Unit;

/// <summary>The files in samples/ are used for manual and E2E testing; each .msgpack must match its .json twin.</summary>
public class SamplesTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    // The API's own reader options, so the samples are checked exactly as the server will read them.
    private static readonly MessagePackSerializerOptions MessagePack = IngestSerialization.MessagePackOptions;

    private static string SamplesDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LogPulse.sln")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("Repository root not found."), "samples");
    }

    private static (T FromJson, T FromMessagePack) Load<T>(string name)
    {
        var dir = SamplesDirectory();
        var fromJson = JsonSerializer.Deserialize<T>(File.ReadAllText(Path.Combine(dir, name + ".json")), Json)!;
        var fromMessagePack = MessagePackSerializer.Deserialize<T>(File.ReadAllBytes(Path.Combine(dir, name + ".msgpack")), MessagePack);
        return (fromJson, fromMessagePack);
    }

    [Fact]
    public void Log_samples_match()
    {
        var (json, msgpack) = Load<IngestLogBatch>("ingest-logs");

        Assert.NotEmpty(json.Entries);
        Assert.Equal(
            json.Entries.Select(e => (e.Timestamp, e.Severity, e.Source, e.Message, e.Exception)),
            msgpack.Entries.Select(e => (e.Timestamp, e.Severity, e.Source, e.Message, e.Exception)));
    }

    [Fact]
    public void Metric_samples_match()
    {
        var (json, msgpack) = Load<IngestMetricBatch>("ingest-metrics");

        Assert.NotEmpty(json.Samples);
        Assert.Equal(
            json.Samples.Select(s => (s.Timestamp, s.CpuPercent, s.MemoryUsedMb, s.MemoryTotalMb, s.DiskUsedPercent)),
            msgpack.Samples.Select(s => (s.Timestamp, s.CpuPercent, s.MemoryUsedMb, s.MemoryTotalMb, s.DiskUsedPercent)));
    }
}
