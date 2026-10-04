using System.ComponentModel.DataAnnotations;
using LogPulse.Api.Controllers;
using LogPulse.Core.Contracts;

namespace LogPulse.Tests.Unit;

public class QueryParametersTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static List<ValidationResult> Validate(object instance)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(instance, new ValidationContext(instance), results, validateAllProperties: true);
        return results;
    }

    [Fact]
    public void Default_log_query_is_valid()
    {
        Assert.Empty(Validate(new LogQueryParameters()));
    }

    [Fact]
    public void Log_query_rejects_long_search_deep_pages_big_pages_and_reversed_ranges()
    {
        Assert.NotEmpty(Validate(new LogQueryParameters { Search = new string('x', LogQueryParameters.MaxSearchLength + 1) }));
        Assert.NotEmpty(Validate(new LogQueryParameters { Page = LogQueryParameters.MaxPage + 1 }));
        Assert.NotEmpty(Validate(new LogQueryParameters { PageSize = 501 }));
        Assert.NotEmpty(Validate(new LogQueryParameters { From = T0, To = T0.AddMinutes(-1) }));
    }

    [Fact]
    public void Metric_range_defaults_to_the_last_hour_and_is_capped_at_24_hours()
    {
        Assert.Equal((T0.AddHours(-1), T0), new MetricRangeParameters().Resolve(T0));
        Assert.Empty(Validate(new MetricRangeParameters { From = T0.AddHours(-24), To = T0 }));
        Assert.NotEmpty(Validate(new MetricRangeParameters { From = T0.AddHours(-24).AddSeconds(-1), To = T0 }));
        Assert.NotEmpty(Validate(new MetricRangeParameters { From = T0, To = T0.AddMinutes(-1) }));
    }

    [Fact]
    public void Ingest_batches_must_have_between_one_and_a_thousand_items()
    {
        Assert.NotEmpty(Validate(new IngestLogBatch()));
        Assert.Empty(Validate(new IngestLogBatch { Entries = [new IngestLogEntry { Source = "s", Message = "m" }] }));
        Assert.NotEmpty(Validate(new IngestLogBatch
        {
            Entries = Enumerable.Range(0, IngestLogBatch.MaxEntries + 1).Select(_ => new IngestLogEntry { Source = "s", Message = "m" }).ToList(),
        }));
        Assert.NotEmpty(Validate(new IngestMetricSample { CpuPercent = 101 }));
    }

    [Fact]
    public void Ingest_batches_reject_null_items_but_accept_blank_lines()
    {
        Assert.NotEmpty(Validate(new IngestLogBatch { Entries = [null!] }));
        Assert.NotEmpty(Validate(new IngestMetricBatch { Samples = [null!] }));
        Assert.Empty(Validate(new IngestLogEntry { Source = string.Empty, Message = string.Empty }));
        Assert.NotEmpty(Validate(new IngestLogEntry { Source = null!, Message = "m" }));
    }
}
