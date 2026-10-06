using System.Net;
using System.Threading.Channels;
using LogPulse.Agent.Logs;
using LogPulse.Agent.Shipping;
using LogPulse.Api.Infrastructure;
using LogPulse.Core.Contracts;
using LogPulse.Core.Models;
using LogPulse.Tests.Fakes;
using MessagePack;

namespace LogPulse.Tests.Unit.Agent;

public class ShippingTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    private static PendingLog Log(string message, string source = "app", long offset = 0) => new(
        new IngestLogEntry { Timestamp = Now, Severity = LogSeverity.Warning, Source = source, Message = message },
        new LogPosition("/var/log/app.log", FileFingerprint.Empty, offset));

    private static ChannelReader<PendingLog> Buffered(IEnumerable<PendingLog> items)
    {
        var channel = Channel.CreateUnbounded<PendingLog>();
        foreach (var item in items)
        {
            channel.Writer.TryWrite(item);
        }

        return channel.Reader;
    }

    private static int SerializedSize(IEnumerable<PendingLog> batch) =>
        MessagePackSerializer.Serialize(new IngestLogBatch { Entries = batch.Select(p => p.Entry).ToList() }, AgentSerialization.MessagePack).Length;

    [Fact]
    public void A_log_batch_stops_at_the_item_limit()
    {
        var reader = Buffered(Enumerable.Range(0, 5).Select(i => Log($"line {i}")));

        Assert.Equal(3, BatchBuilder.TakeLogs(reader, maxItems: 3, maxBytes: 1024 * 1024).Count);
        Assert.Equal(2, BatchBuilder.TakeLogs(reader, maxItems: 3, maxBytes: 1024 * 1024).Count);
        Assert.Empty(BatchBuilder.TakeLogs(reader, maxItems: 3, maxBytes: 1024 * 1024));
    }

    [Fact]
    public void A_log_batch_stops_before_the_byte_limit_measured_on_the_real_payload()
    {
        // The worst case for UTF-8: 3 bytes per character, every field at its maximum length.
        var worst = Log(new string('€', FieldLimits.LogMessage), source: new string('€', FieldLimits.LogSource));
        var reader = Buffered(Enumerable.Repeat(worst, 100));
        const int maxBytes = 256 * 1024;

        var batch = BatchBuilder.TakeLogs(reader, maxItems: 1000, maxBytes);

        Assert.InRange(batch.Count, 2, 99);
        Assert.True(SerializedSize(batch) <= maxBytes, $"{SerializedSize(batch)} bytes");
    }

    [Fact]
    public void The_size_estimate_is_an_upper_bound_of_the_serialized_entry()
    {
        var entries = new[]
        {
            Log(""),
            Log("short", source: ""),
            Log(string.Concat(Enumerable.Repeat("😀", FieldLimits.LogMessage / 2)), source: new string('é', FieldLimits.LogSource)),
            Log(new string('€', FieldLimits.LogMessage)),
        };

        foreach (var entry in entries)
        {
            Assert.True(SerializedSize([entry]) <= BatchBuilder.EstimateBytes(entry.Entry) + 32);
        }
    }

    [Fact]
    public void A_metric_batch_stops_at_the_item_limit()
    {
        var channel = Channel.CreateUnbounded<IngestMetricSample>();
        for (var i = 0; i < 4; i++)
        {
            channel.Writer.TryWrite(new IngestMetricSample { Timestamp = Now, CpuPercent = i });
        }

        Assert.Equal([0d, 1d, 2d], BatchBuilder.TakeMetrics(channel.Reader, maxItems: 3).Select(s => s.CpuPercent));
        Assert.Single(BatchBuilder.TakeMetrics(channel.Reader, maxItems: 3));
    }

    [Fact]
    public void Batches_serialized_by_the_agent_are_read_by_the_api()
    {
        var logs = new IngestLogBatch { Entries = [Log("disk almost full", source: "Disk").Entry] };
        var metrics = new IngestMetricBatch { Samples = [new IngestMetricSample { Timestamp = Now, CpuPercent = 12.5, MemoryUsedMb = 2048, MemoryTotalMb = 8192, DiskUsedPercent = 61.2 }] };

        // The API's own reader options, with its batch limits and strict key matching.
        var readLogs = MessagePackSerializer.Deserialize<IngestLogBatch>(
            MessagePackSerializer.Serialize(logs, AgentSerialization.MessagePack), IngestSerialization.MessagePackOptions);
        var readMetrics = MessagePackSerializer.Deserialize<IngestMetricBatch>(
            MessagePackSerializer.Serialize(metrics, AgentSerialization.MessagePack), IngestSerialization.MessagePackOptions);

        var entry = Assert.Single(readLogs.Entries);
        Assert.Equal((Now, LogSeverity.Warning, "Disk", "disk almost full"), (entry.Timestamp, entry.Severity, entry.Source, entry.Message));
        var sample = Assert.Single(readMetrics.Samples);
        Assert.Equal((12.5, 2048L, 8192L, 61.2), (sample.CpuPercent, sample.MemoryUsedMb, sample.MemoryTotalMb, sample.DiskUsedPercent));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, SendOutcome.Rejected)]
    [InlineData(HttpStatusCode.UnprocessableEntity, SendOutcome.Rejected)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, SendOutcome.TooLarge)] // maybe a proxy's lower limit: split, don't drop
    [InlineData(HttpStatusCode.UnsupportedMediaType, SendOutcome.RetryLater)] // never about the batch itself
    [InlineData(HttpStatusCode.Unauthorized, SendOutcome.RetryLater)] // a fresh token was refused too: keep the data
    [InlineData(HttpStatusCode.Forbidden, SendOutcome.RetryLater)]
    [InlineData(HttpStatusCode.NotFound, SendOutcome.RetryLater)] // wrong base URL: a configuration problem
    [InlineData(HttpStatusCode.TooManyRequests, SendOutcome.RetryLater)]
    [InlineData(HttpStatusCode.ServiceUnavailable, SendOutcome.RetryLater)]
    public async Task Only_payload_errors_drop_a_batch(HttpStatusCode status, SendOutcome expected)
    {
        var client = new IngestClient(new StubHttpClientFactory(new StubHttpHandler(_ => new HttpResponseMessage(status))));

        var result = await client.SendLogsAsync(new IngestLogBatch { Entries = [Log("x").Entry] }, CancellationToken.None);

        Assert.Equal(expected, result.Outcome);
    }

    [Fact]
    public async Task An_accepted_batch_reports_the_viewers_flag_and_travels_as_messagepack()
    {
        var handler = new StubHttpHandler(_ => StubHttpHandler.Json(new IngestResult(1, ViewersOnline: false)));
        var client = new IngestClient(new StubHttpClientFactory(handler));

        var result = await client.SendMetricsAsync(new IngestMetricBatch { Samples = [new IngestMetricSample { Timestamp = Now }] }, CancellationToken.None);

        Assert.Equal(SendResult.Accepted(viewersOnline: false), result);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(("/api/ingest/metrics", "application/x-msgpack"), (request.Path, request.ContentType));
    }

    [Fact]
    public async Task A_success_response_that_is_not_the_api_json_means_retry_later()
    {
        // e.g. a proxy's maintenance page with status 200
        var html = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>maintenance</html>") });
        var client = new IngestClient(new StubHttpClientFactory(html));

        var result = await client.SendLogsAsync(new IngestLogBatch { Entries = [Log("x").Entry] }, CancellationToken.None);

        Assert.Equal(SendOutcome.RetryLater, result.Outcome);
    }

    [Fact]
    public async Task A_response_without_the_viewers_flag_keeps_the_full_rate()
    {
        var handler = new StubHttpHandler(_ => StubHttpHandler.Json(new { accepted = 1 }));
        var client = new IngestClient(new StubHttpClientFactory(handler));

        var result = await client.SendLogsAsync(new IngestLogBatch { Entries = [Log("x").Entry] }, CancellationToken.None);

        Assert.Equal(SendResult.Accepted(viewersOnline: true), result);
    }

    [Fact]
    public async Task A_network_error_means_retry_later()
    {
        var client = new IngestClient(new StubHttpClientFactory(new StubHttpHandler(_ => throw new HttpRequestException("connection refused"))));

        var result = await client.SendLogsAsync(new IngestLogBatch { Entries = [Log("x").Entry] }, CancellationToken.None);

        Assert.Equal(SendOutcome.RetryLater, result.Outcome);
    }
}
