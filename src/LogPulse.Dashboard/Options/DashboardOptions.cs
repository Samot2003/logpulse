using System.ComponentModel.DataAnnotations;

namespace LogPulse.Dashboard.Options;

public sealed class DashboardOptions : IValidatableObject
{
    public const string SectionName = "Dashboard";

    /// <summary>Base address of the LogPulse API, e.g. https://logpulse.example.com/.</summary>
    [Required]
    public Uri? ApiBaseUrl { get; set; }

    /// <summary>
    /// A server is shown offline when it has not reported for this long. Agents report at least every
    /// 30 seconds (metrics while nobody watches), so the default leaves room for two missed reports.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:10", "1.00:00:00")]
    public TimeSpan OfflineAfter { get; set; } = TimeSpan.FromSeconds(90);

    /// <summary>Minimum time between two automatic reloads of a log list that follows new entries live.</summary>
    [Range(typeof(TimeSpan), "00:00:00.5", "00:01:00")]
    public TimeSpan LiveRefreshInterval { get; set; } = TimeSpan.FromSeconds(2);

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (ApiBaseUrl is { } url && (!url.IsAbsoluteUri || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps)))
        {
            yield return new ValidationResult("ApiBaseUrl must be an absolute http or https URL.", [nameof(ApiBaseUrl)]);
        }
    }
}
