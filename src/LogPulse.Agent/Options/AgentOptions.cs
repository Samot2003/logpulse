using System.ComponentModel.DataAnnotations;
using LogPulse.Core.Contracts;
using LogPulse.Core.Models;

namespace LogPulse.Agent.Options;

public sealed class AgentOptions : IValidatableObject
{
    public const string SectionName = "Agent";

    /// <summary>Base address of the LogPulse API, e.g. https://logpulse.example.com/.</summary>
    [Required]
    public Uri? ApiBaseUrl { get; set; }

    /// <summary>The name this server reports as; it must match the agent credential created in the API.</summary>
    [Required]
    [StringLength(FieldLimits.ServerName)]
    [RegularExpression(FieldLimits.ServerNamePattern)]
    public string ServerName { get; set; } = string.Empty;

    /// <summary>The agent's API key. Never commit a real one: use an environment variable or user secrets.</summary>
    [Required]
    [StringLength(256)]
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Time between metric samples while someone is watching the dashboard.</summary>
    [Range(typeof(TimeSpan), "00:00:00.1", "01:00:00")]
    public TimeSpan MetricsInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Time between metric samples while nobody is watching.</summary>
    [Range(typeof(TimeSpan), "00:00:00.1", "01:00:00")]
    public TimeSpan IdleMetricsInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How often buffered logs and metrics are sent.</summary>
    [Range(typeof(TimeSpan), "00:00:00.1", "00:10:00")]
    public TimeSpan SendInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How often the log files are checked for new lines.</summary>
    [Range(typeof(TimeSpan), "00:00:00.1", "00:10:00")]
    public TimeSpan LogPollInterval { get; set; } = TimeSpan.FromSeconds(1);

    [Range(1, IngestLogBatch.MaxEntries)]
    public int MaxBatchItems { get; set; } = 500;

    /// <summary>
    /// Upper bound for one request body. The minimum leaves room for one entry with every field at its maximum
    /// length; the maximum is what the API accepts. The default stays below common proxy limits (nginx: 1 MB).
    /// </summary>
    [Range(256 * 1024, IngestLimits.MaxRequestBytes)]
    public int MaxBatchBytes { get; set; } = 512 * 1024;

    /// <summary>Items held in memory per buffer (logs and metrics) while the API is slow or unreachable.</summary>
    [Range(100, 1_000_000)]
    public int BufferCapacity { get; set; } = 10_000;

    /// <summary>Where read positions of the log files are kept. Relative paths start at the content root.</summary>
    [Required]
    public string StateDirectory { get; set; } = "state";

    /// <summary>Path whose drive is reported as disk usage. Defaults to the system drive.</summary>
    public string? DiskPath { get; set; }

    /// <summary>Read the existing content of a log file seen for the first time, instead of only new lines.</summary>
    public bool ReadExistingLogs { get; set; }

    /// <summary>
    /// Allows plain HTTP to a remote API outside Development. Only for a private network that nobody else can sniff,
    /// such as the docker compose network of the demo: the API key and the tokens travel unencrypted.
    /// </summary>
    public bool AllowInsecureHttp { get; set; }

    public List<LogFileOptions> LogFiles { get; set; } = [];

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (ApiBaseUrl is { } url && (!url.IsAbsoluteUri || (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp)))
        {
            yield return new ValidationResult("ApiBaseUrl must be an absolute http or https address.", [nameof(ApiBaseUrl)]);
        }

        if (IdleMetricsInterval < MetricsInterval)
        {
            yield return new ValidationResult("IdleMetricsInterval cannot be shorter than MetricsInterval.", [nameof(IdleMetricsInterval)]);
        }

        // Data annotations do not look inside list items.
        for (var i = 0; i < LogFiles.Count; i++)
        {
            if (LogFiles[i] is not { Path.Length: > 0 } file || string.IsNullOrWhiteSpace(file.Path))
            {
                yield return new ValidationResult($"LogFiles[{i}] needs a Path.", [nameof(LogFiles)]);
            }
            else if (file.Source is { Length: > FieldLimits.LogSource })
            {
                yield return new ValidationResult($"LogFiles[{i}].Source is longer than {FieldLimits.LogSource} characters.", [nameof(LogFiles)]);
            }
        }
    }
}

public sealed class LogFileOptions
{
    /// <summary>File to follow. Relative paths start at the content root.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Reported as the source of every line. Defaults to the file name.</summary>
    public string? Source { get; set; }
}
