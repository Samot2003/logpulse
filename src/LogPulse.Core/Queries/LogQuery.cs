using LogPulse.Core.Models;

namespace LogPulse.Core.Queries;

/// <summary>Filters for the log viewer. Every filter is optional; results are newest first.</summary>
public sealed record LogQuery
{
    public const int MaxPageSize = 500;

    public int? ServerId { get; init; }
    public LogSeverity? MinSeverity { get; init; }

    /// <summary>Inclusive lower bound.</summary>
    public DateTimeOffset? From { get; init; }

    /// <summary>Inclusive upper bound.</summary>
    public DateTimeOffset? To { get; init; }

    public string? Search { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 50;

    /// <summary>Rows to skip for the current page. Computed as long so huge page numbers cannot overflow.</summary>
    public long Offset => (long)(Math.Max(1, Page) - 1) * Math.Clamp(PageSize, 1, MaxPageSize);

    /// <summary>
    /// Returns a copy with page and page size clamped to valid ranges and a blank search removed.
    /// The search is never shortened: a truncated search would match lines that do not contain it.
    /// </summary>
    public LogQuery Normalized() => this with
    {
        Page = Math.Max(1, Page),
        PageSize = Math.Clamp(PageSize, 1, MaxPageSize),
        Search = string.IsNullOrWhiteSpace(Search) ? null : Search.Trim(),
    };
}
