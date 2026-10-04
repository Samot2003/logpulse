using System.ComponentModel.DataAnnotations;
using LogPulse.Core.Models;
using LogPulse.Core.Queries;

namespace LogPulse.Api.Controllers;

public sealed class LogQueryParameters : IValidatableObject
{
    /// <summary>
    /// Escaping can double the length and LIKE patterns are limited to about 4000 characters,
    /// so 500 leaves plenty of margin while covering any realistic search.
    /// </summary>
    public const int MaxSearchLength = 500;

    /// <summary>Deep pages cost a long index scan each; nobody pages this far in a log viewer.</summary>
    public const int MaxPage = 10_000;

    public int? ServerId { get; set; }

    [EnumDataType(typeof(LogSeverity))]
    public LogSeverity? MinSeverity { get; set; }

    public DateTimeOffset? From { get; set; }

    public DateTimeOffset? To { get; set; }

    [StringLength(MaxSearchLength)]
    public string? Search { get; set; }

    [Range(1, MaxPage)]
    public int Page { get; set; } = 1;

    [Range(1, LogQuery.MaxPageSize)]
    public int PageSize { get; set; } = 50;

    public LogQuery ToQuery() => new()
    {
        ServerId = ServerId,
        MinSeverity = MinSeverity,
        From = From,
        To = To,
        Search = Search,
        Page = Page,
        PageSize = PageSize,
    };

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (From is { } from && To is { } to && from > to)
        {
            yield return new ValidationResult("'from' must not be later than 'to'.", [nameof(From), nameof(To)]);
        }
    }
}

public sealed class MetricRangeParameters : IValidatableObject
{
    /// <summary>A day of samples is enough for any chart and keeps responses small.</summary>
    public static readonly TimeSpan MaxRange = TimeSpan.FromHours(24);

    public static readonly TimeSpan DefaultRange = TimeSpan.FromHours(1);

    /// <summary>Defaults to one hour before <see cref="To"/>.</summary>
    public DateTimeOffset? From { get; set; }

    /// <summary>Defaults to now.</summary>
    public DateTimeOffset? To { get; set; }

    public (DateTimeOffset From, DateTimeOffset To) Resolve(DateTimeOffset now)
    {
        var to = To ?? now;
        return (From ?? to - DefaultRange, to);
    }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (From is not { } from)
        {
            yield break;
        }

        // When 'to' is omitted, validate against "now" the same way Resolve will, using the app's clock.
        var time = validationContext.GetService(typeof(TimeProvider)) as TimeProvider ?? TimeProvider.System;
        var to = To ?? time.GetUtcNow();
        if (from > to)
        {
            yield return new ValidationResult("'from' must not be later than 'to'.", [nameof(From), nameof(To)]);
        }
        else if (to - from > MaxRange)
        {
            yield return new ValidationResult($"The range cannot exceed {MaxRange.TotalHours} hours.", [nameof(From), nameof(To)]);
        }
    }
}
