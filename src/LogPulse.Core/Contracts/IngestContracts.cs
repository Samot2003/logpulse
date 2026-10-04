using System.ComponentModel.DataAnnotations;
using LogPulse.Core.Models;

namespace LogPulse.Core.Contracts;

// Wire contracts shared by the API and the agent. They travel as JSON or MessagePack (string keys),
// so they are plain classes with public setters and no serializer-specific attributes.

public static class IngestLimits
{
    /// <summary>
    /// Maximum request body. Agents should truncate fields to <see cref="FieldLimits"/> and split batches so a
    /// request stays below this size: 1000 entries with 4000-character messages already come close to it.
    /// </summary>
    public const long MaxRequestBytes = 4 * 1024 * 1024;

    /// <summary>Timestamps further than this into the future (by the API's clock) are clamped to the receive time.</summary>
    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(5);
}

public sealed class IngestLogEntry
{
    /// <summary>When the event happened, by the agent's clock. Future values are clamped on ingestion.</summary>
    public DateTimeOffset Timestamp { get; set; }

    [EnumDataType(typeof(LogSeverity))]
    public LogSeverity Severity { get; set; }

    /// <summary>May be empty (stored as "unknown"); longer values are truncated to <see cref="FieldLimits.LogSource"/>.</summary>
    [Required(AllowEmptyStrings = true)]
    public string Source { get; set; } = string.Empty;

    /// <summary>May be empty (a blank log line); longer values are truncated to <see cref="FieldLimits.LogMessage"/>.</summary>
    [Required(AllowEmptyStrings = true)]
    public string Message { get; set; } = string.Empty;

    public string? Exception { get; set; }
}

public sealed class IngestLogBatch : IValidatableObject
{
    public const int MaxEntries = 1000;

    [Required]
    [MinLength(1)]
    [MaxLength(MaxEntries)]
    public List<IngestLogEntry> Entries { get; set; } = [];

    // [Required] and [MinLength] do not look inside the list, so a null element would otherwise reach the service.
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext) =>
        Entries.Contains(null!)
            ? [new ValidationResult("Entries must not contain null items.", [nameof(Entries)])]
            : [];
}

public sealed class IngestMetricSample
{
    /// <summary>When the sample was taken, by the agent's clock. Future values are clamped on ingestion.</summary>
    public DateTimeOffset Timestamp { get; set; }

    [Range(0, 100)]
    public double CpuPercent { get; set; }

    [Range(0, long.MaxValue)]
    public long MemoryUsedMb { get; set; }

    [Range(0, long.MaxValue)]
    public long MemoryTotalMb { get; set; }

    [Range(0, 100)]
    public double DiskUsedPercent { get; set; }
}

public sealed class IngestMetricBatch : IValidatableObject
{
    public const int MaxSamples = 1000;

    [Required]
    [MinLength(1)]
    [MaxLength(MaxSamples)]
    public List<IngestMetricSample> Samples { get; set; } = [];

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext) =>
        Samples.Contains(null!)
            ? [new ValidationResult("Samples must not contain null items.", [nameof(Samples)])]
            : [];
}

public sealed record IngestResult(int Accepted);
