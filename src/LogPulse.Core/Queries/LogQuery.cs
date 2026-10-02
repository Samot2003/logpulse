using LogPulse.Core.Models;

namespace LogPulse.Core.Queries;

/// <summary>Filters for the log viewer. Every filter is optional; results are newest first.</summary>
public sealed record LogQuery
{
    public const int MaxPageSize = 500;

    public int? ServerId { get; init; }
    public LogSeverity? MinSeverity { get; init; }
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
    public string? Search { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 50;

    /// <summary>Returns a copy with page and page size clamped to valid ranges.</summary>
    public LogQuery Normalized() => this with
    {
        Page = Math.Max(1, Page),
        PageSize = Math.Clamp(PageSize, 1, MaxPageSize),
        Search = string.IsNullOrWhiteSpace(Search) ? null : Search.Trim(),
    };
}
