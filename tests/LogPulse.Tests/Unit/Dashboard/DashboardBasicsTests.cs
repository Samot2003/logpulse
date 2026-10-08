using System.ComponentModel.DataAnnotations;
using LogPulse.Core.Models;
using LogPulse.Dashboard.Api;
using LogPulse.Dashboard.Auth;
using LogPulse.Dashboard.Components;
using LogPulse.Dashboard.Components.Charts;
using LogPulse.Dashboard.Live;
using LogPulse.Dashboard.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Time.Testing;

namespace LogPulse.Tests.Unit.Dashboard;

public class ChartGeometryTests
{
    private static readonly DateTimeOffset From = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = From.AddMinutes(10);
    private static readonly TimeSpan Gap = TimeSpan.FromSeconds(90);

    private static IReadOnlyList<string> Lines(params ChartPoint[] points) =>
        ChartGeometry.Lines(points, From, To, max: 100, width: 600, height: 100, Gap);

    [Fact]
    public void Time_maps_to_x_and_value_to_y_with_zero_at_the_bottom()
    {
        ChartPoint[] points = [new(From, 0), new(From.AddMinutes(5), 50), new(To, 100)];

        var line = Assert.Single(ChartGeometry.Lines(points, From, To, max: 100, width: 600, height: 100, gap: TimeSpan.FromMinutes(5)));

        Assert.Equal("0,100 300,50 600,0", line);
    }

    [Fact]
    public void A_pause_longer_than_the_gap_starts_a_new_line()
    {
        var lines = Lines(new(From, 10), new(From.AddSeconds(30), 20), new(From.AddMinutes(5), 30), new(From.AddMinutes(5).AddSeconds(30), 40));

        Assert.Equal(2, lines.Count);
    }

    [Fact]
    public void Samples_in_the_same_pixel_are_averaged_so_long_ranges_stay_small()
    {
        // 24 hours at one sample per second would be 86,400 points; one per pixel column is enough.
        var day = Enumerable.Range(0, 86_400).Select(s => new ChartPoint(From.AddSeconds(s), s % 2 == 0 ? 10 : 30));

        var line = Assert.Single(ChartGeometry.Lines(day, From, From.AddDays(1), 100, 600, 100, Gap));

        var points = line.Split(' ');
        Assert.InRange(points.Length, 590, 601);
        // Average of 10 % and 30 % → 20 %, drawn 80 px from the top (± rounding of the samples per pixel).
        Assert.All(points, p => Assert.InRange(double.Parse(p.Split(',')[1], System.Globalization.CultureInfo.InvariantCulture), 79.5, 80.5));
    }

    [Fact]
    public void Points_outside_the_window_or_not_finite_are_ignored_and_values_are_clamped()
    {
        var line = Assert.Single(Lines(new(From.AddMinutes(-1), 50), new(From, double.NaN), new(From.AddMinutes(5), 150), new(To.AddMinutes(1), 50)));

        Assert.Equal("300,0", line);
    }

    [Fact]
    public void An_empty_window_or_no_points_draw_nothing()
    {
        Assert.Empty(Lines());
        Assert.Empty(ChartGeometry.Lines([new(From, 1)], From, From, 100, 600, 100, Gap));
    }
}

public class DisplayTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(2, "just now")]
    [InlineData(42, "42 s ago")]
    [InlineData(150, "2 min ago")]
    [InlineData(7200, "2 h ago")]
    [InlineData(200_000, "2 d ago")]
    public void Ago_is_short_and_relative(int seconds, string expected) =>
        Assert.Equal(expected, Display.Ago(Now.AddSeconds(-seconds), Now));

    [Fact]
    public void Times_are_shown_in_utc_whatever_their_offset() =>
        Assert.Equal("2026-01-01 12:00:00", Display.Time(Now.ToOffset(TimeSpan.FromHours(2))));

    [Fact]
    public void Memory_percent_needs_a_total()
    {
        Assert.Equal(25, Display.MemoryPercent(new MetricSample { MemoryUsedMb = 1024, MemoryTotalMb = 4096 }));
        Assert.Null(Display.MemoryPercent(new MetricSample { MemoryUsedMb = 1024, MemoryTotalMb = 0 }));
    }

    [Theory]
    [InlineData(null, "unknown")]
    [InlineData(10.0, "ok")]
    [InlineData(75.0, "warn")]
    [InlineData(95.0, "critical")]
    public void Usage_levels(double? percent, string expected) => Assert.Equal(expected, Display.Level(percent));

    [Fact]
    public void Numbers_use_the_invariant_culture()
    {
        Assert.Equal("12.5 %", Display.Percent(12.5));
        Assert.Equal("1.5 GB", Display.Gigabytes(1536));
    }
}

public class LocalUrlTests
{
    [Theory]
    [InlineData("/logs?serverId=2", "/logs?serverId=2")]
    [InlineData("/", "/")]
    [InlineData(null, "/")]
    [InlineData("", "/")]
    [InlineData("logs", "/")]
    [InlineData("https://evil.example/", "/")]
    [InlineData("//evil.example/", "/")]
    [InlineData("/\\evil.example/", "/")]
    [InlineData("/\r\nSet-Cookie: x", "/")]
    public void Only_paths_on_this_site_are_followed_after_login(string? returnUrl, string expected) =>
        Assert.Equal(expected, LocalUrl.OrHome(returnUrl));
}

public class DashboardOptionsTests
{
    private static List<ValidationResult> Validate(DashboardOptions options)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true);
        return results;
    }

    private static HostingEnvironment Environment(string name) => new() { EnvironmentName = name };

    [Fact]
    public void Defaults_with_an_api_address_are_valid() =>
        Assert.Empty(Validate(new DashboardOptions { ApiBaseUrl = new Uri("https://logpulse.example.com/") }));

    [Fact]
    public void The_api_address_is_required_and_must_be_http_or_https()
    {
        Assert.NotEmpty(Validate(new DashboardOptions()));
        Assert.NotEmpty(Validate(new DashboardOptions { ApiBaseUrl = new Uri("ftp://logpulse.example.com/") }));
        Assert.NotEmpty(Validate(new DashboardOptions { ApiBaseUrl = new Uri("/api", UriKind.Relative) }));
    }

    [Fact]
    public void Plain_http_to_a_remote_api_is_refused_outside_development()
    {
        var remote = new DashboardOptions { ApiBaseUrl = new Uri("http://logpulse.example.com/") };

        Assert.Throws<InvalidOperationException>(() => DashboardStartupChecks.Check(remote, Environment(Environments.Production)));
        DashboardStartupChecks.Check(remote, Environment(Environments.Development));
        DashboardStartupChecks.Check(new DashboardOptions { ApiBaseUrl = new Uri("http://localhost:5080/") }, Environment(Environments.Production));
        DashboardStartupChecks.Check(new DashboardOptions { ApiBaseUrl = new Uri("https://logpulse.example.com/") }, Environment(Environments.Production));
    }

    [Fact]
    public void Plain_http_on_a_private_network_needs_an_explicit_opt_in()
    {
        var compose = new DashboardOptions { ApiBaseUrl = new Uri("http://api:8080/"), AllowInsecureHttp = true };

        DashboardStartupChecks.Check(compose, Environment(Environments.Production));
        Assert.False(new DashboardOptions().AllowInsecureHttp);
    }

    [Fact]
    public void The_api_address_keeps_its_base_path()
    {
        Assert.Equal("https://host/logpulse/", ApiAddress.Normalize(new Uri("https://host/logpulse")).AbsoluteUri);
        Assert.Equal("https://host/", ApiAddress.Normalize(new Uri("https://host/")).AbsoluteUri);
    }

    [Fact]
    public void Api_errors_are_described_without_details()
    {
        Assert.Contains("not reachable", ApiErrors.Describe(new HttpRequestException("connection refused")));
        Assert.Contains("Too many", ApiErrors.Describe(new HttpRequestException("x", null, System.Net.HttpStatusCode.TooManyRequests)));
        Assert.Contains("500", ApiErrors.Describe(new HttpRequestException("x", null, System.Net.HttpStatusCode.InternalServerError)));
        Assert.Contains("too long", ApiErrors.Describe(new TaskCanceledException()));
        Assert.False(ApiErrors.IsApiFailure(new SessionExpiredException()));
    }
}

public class RefreshThrottleTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
    private int _refreshes;

    private RefreshThrottle Throttle() => new(Interval, _time, () =>
    {
        _refreshes++;
        return Task.CompletedTask;
    });

    [Fact]
    public void The_first_request_refreshes_at_once_and_a_burst_after_it_merges_into_one()
    {
        using var throttle = Throttle();

        throttle.Request();
        Assert.Equal(1, _refreshes);

        throttle.Request();
        throttle.Request();
        throttle.Request();
        Assert.Equal(1, _refreshes);

        _time.Advance(Interval);
        Assert.Equal(2, _refreshes);
        _time.Advance(Interval * 3);
        Assert.Equal(2, _refreshes);
    }

    [Fact]
    public void Nothing_runs_after_dispose()
    {
        var throttle = Throttle();
        throttle.Request();
        throttle.Request();

        throttle.Dispose();
        _time.Advance(Interval);
        throttle.Request();

        Assert.Equal(1, _refreshes);
    }
}
