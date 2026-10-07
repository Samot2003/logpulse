using Bunit;
using Bunit.TestDoubles;
using LogPulse.Core.Contracts;
using LogPulse.Core.Models;
using LogPulse.Dashboard.Api;
using LogPulse.Dashboard.Auth;
using LogPulse.Dashboard.Components.Charts;
using LogPulse.Dashboard.Components.Pages;
using LogPulse.Dashboard.Components.Shared;
using LogPulse.Dashboard.Live;
using LogPulse.Dashboard.Options;
using LogPulse.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace LogPulse.Tests.Unit.Dashboard;

/// <summary>Base for page tests: a fake API, a live feed driven by the test and a fake clock.</summary>
public abstract class DashboardPageTests : BunitContext
{
    protected static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    protected DashboardPageTests()
    {
        Services.AddSingleton<ILogPulseApi>(Api);
        Services.AddSingleton<ILiveFeed>(Feed);
        Services.AddSingleton<TimeProvider>(Time);
        Services.AddSingleton(Microsoft.Extensions.Options.Options.Create(Settings));
    }

    internal FakeLogPulseApi Api { get; } = new();

    internal FakeLiveFeed Feed { get; } = new();

    protected FakeTimeProvider Time { get; } = new(Now);

    protected DashboardOptions Settings { get; } = new()
    {
        ApiBaseUrl = new Uri("http://api.test/"),
        OfflineAfter = TimeSpan.FromSeconds(90),
        LiveRefreshInterval = TimeSpan.FromSeconds(2),
    };

    protected BunitNavigationManager Navigation => Services.GetRequiredService<BunitNavigationManager>();

    protected Server AddServer(int id, string name, TimeSpan seenAgo)
    {
        var server = new Server(id, name, Now.AddDays(-3), Now - seenAgo);
        Api.Servers.Add(server);
        return server;
    }

    protected static MetricSample Sample(int serverId, DateTimeOffset at, double cpu = 20, long usedMb = 2048, long totalMb = 8192, double disk = 40) =>
        new() { ServerId = serverId, Timestamp = at, CpuPercent = cpu, MemoryUsedMb = usedMb, MemoryTotalMb = totalMb, DiskUsedPercent = disk };

    protected static LogEntry Log(long id, int serverId, LogSeverity severity, string message, DateTimeOffset at) =>
        new() { Id = id, ServerId = serverId, Severity = severity, Source = "app", Message = message, Timestamp = at };
}

public class OverviewPageTests : DashboardPageTests
{
    [Fact]
    public void Each_server_shows_its_status_and_latest_usage()
    {
        AddServer(1, "web-01", TimeSpan.FromSeconds(10));
        AddServer(2, "db-01", TimeSpan.FromMinutes(10));
        Api.Metrics.Add(Sample(1, Now.AddMinutes(-1), cpu: 10));
        Api.Metrics.Add(Sample(1, Now.AddSeconds(-10), cpu: 35.5, usedMb: 6144, totalMb: 8192, disk: 91));

        var page = Render<Overview>();

        var cards = page.FindAll(".server-card");
        Assert.Equal(2, cards.Count);
        Assert.Contains("2 servers, 1 online", page.Markup, StringComparison.Ordinal);
        var web = cards.Single(c => c.TextContent.Contains("web-01", StringComparison.Ordinal));
        Assert.Contains("Online", web.QuerySelector(".status")!.TextContent, StringComparison.Ordinal);
        Assert.Contains("35.5 %", web.TextContent, StringComparison.Ordinal);
        Assert.Contains("6.0 GB of 8.0 GB", web.TextContent, StringComparison.Ordinal);
        Assert.NotNull(web.QuerySelector(".usage-critical")); // disk at 91 %
        var db = cards.Single(c => c.TextContent.Contains("db-01", StringComparison.Ordinal));
        Assert.Contains("Offline", db.QuerySelector(".status")!.TextContent, StringComparison.Ordinal);
        Assert.Contains("No metrics yet", db.TextContent, StringComparison.Ordinal);
        Assert.Equal("servers/1", web.QuerySelector("a.server-name")!.GetAttribute("href"));
    }

    [Fact]
    public void Without_servers_it_explains_how_to_start_an_agent()
    {
        var page = Render<Overview>();

        Assert.Contains("No server has reported yet", page.Markup, StringComparison.Ordinal);
        Assert.Contains("dotnet run --project src/LogPulse.Agent", page.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void The_page_opens_the_live_feed_and_live_metrics_update_the_card()
    {
        AddServer(1, "web-01", TimeSpan.FromMinutes(5));
        Api.Metrics.Add(Sample(1, Now.AddMinutes(-5), cpu: 10));
        var page = Render<Overview>();
        Assert.Equal(1, Feed.Starts);
        Assert.Contains("Offline", page.Find(".status").TextContent, StringComparison.Ordinal);

        Feed.Push(new MetricsReceivedEvent(1, "web-01", [Sample(1, Now, cpu: 77)], Now));

        page.WaitForAssertion(() =>
        {
            Assert.Contains("77 %", page.Find(".server-card").TextContent, StringComparison.Ordinal);
            Assert.Contains("Online", page.Find(".status").TextContent, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void A_server_reporting_for_the_first_time_is_added()
    {
        var page = Render<Overview>();
        Assert.Empty(page.FindAll(".server-card"));

        AddServer(5, "new-01", TimeSpan.Zero);
        Feed.Push(new LogsReceivedEvent(5, "new-01", 3, LogSeverity.Information, Now));

        page.WaitForAssertion(() => Assert.Single(page.FindAll(".server-card")));
    }

    [Fact]
    public void Servers_go_offline_with_time_alone()
    {
        AddServer(1, "web-01", TimeSpan.FromSeconds(30));
        var page = Render<Overview>();
        Assert.Contains("Online", page.Find(".status").TextContent, StringComparison.Ordinal);

        Time.Advance(TimeSpan.FromMinutes(2));

        page.WaitForAssertion(() => Assert.Contains("Offline", page.Find(".status").TextContent, StringComparison.Ordinal));
    }

    [Fact]
    public void An_unavailable_api_shows_a_notice_and_retrying_recovers()
    {
        AddServer(1, "web-01", TimeSpan.Zero);
        Api.Failure = new HttpRequestException("connection refused");
        var page = Render<Overview>();
        Assert.Contains("not reachable", page.Find(".notice-error").TextContent, StringComparison.Ordinal);

        Api.Failure = null;
        page.Find(".notice-error button").Click();

        Assert.Empty(page.FindAll(".notice-error"));
        Assert.Single(page.FindAll(".server-card"));
    }

    [Fact]
    public void An_expired_session_sends_the_user_to_the_login_page()
    {
        Api.Failure = new SessionExpiredException();

        Render<Overview>();

        Assert.StartsWith("http://localhost/login?expired=1&returnUrl=%2F", Navigation.Uri, StringComparison.Ordinal);
    }

    [Fact]
    public void After_a_reconnection_the_data_is_reloaded()
    {
        AddServer(1, "web-01", TimeSpan.Zero);
        Render<Overview>();
        var calls = Api.ServerCalls;

        Feed.SetStatus(LiveStatus.Reconnecting);
        Feed.SetStatus(LiveStatus.Live);

        Assert.Equal(calls + 1, Api.ServerCalls);
    }

    [Fact]
    public async Task Leaving_the_page_unsubscribes_from_the_feed()
    {
        Render<Overview>();
        Assert.True(Feed.HasSubscribers);

        await DisposeComponentsAsync();

        Assert.False(Feed.HasSubscribers);
    }
}

public class ServerDetailPageTests : DashboardPageTests
{
    [Fact]
    public void Charts_show_the_last_hour_and_the_recent_logs_are_listed()
    {
        AddServer(1, "web-01", TimeSpan.FromSeconds(5));
        Api.Metrics.Add(Sample(1, Now.AddMinutes(-30), cpu: 20));
        Api.Metrics.Add(Sample(1, Now.AddMinutes(-1), cpu: 60));
        Api.Metrics.Add(Sample(1, Now.AddHours(-2), cpu: 99)); // outside the default range
        Api.Logs.Add(Log(1, 1, LogSeverity.Error, "disk full", Now.AddMinutes(-2)));

        var page = Render<ServerDetail>(ps => ps.Add(p => p.ServerId, 1));

        Assert.Equal("web-01", page.Find("h1").TextContent);
        Assert.Equal((1, Now.AddHours(-1), Now), Assert.Single(Api.MetricQueries));
        Assert.Equal(3, page.FindAll(".chart").Count);
        Assert.Contains("60 %", page.FindAll(".chart")[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("disk full", page.Find("table.logs").TextContent, StringComparison.Ordinal);
        Assert.Equal("logs?serverId=1", page.Find(".card-header a").GetAttribute("href"));
    }

    [Fact]
    public void An_unknown_server_says_so()
    {
        var page = Render<ServerDetail>(ps => ps.Add(p => p.ServerId, 42));

        Assert.Contains("Server not found", page.Markup, StringComparison.Ordinal);
        Assert.Empty(Api.MetricQueries);
    }

    [Fact]
    public void Choosing_a_range_reloads_the_metrics_for_it()
    {
        AddServer(1, "web-01", TimeSpan.Zero);
        var page = Render<ServerDetail>(ps => ps.Add(p => p.ServerId, 1));

        page.FindAll(".chip").Single(b => b.TextContent == "24 hours").Click();

        Assert.Equal((1, Now.AddHours(-24), Now), Api.MetricQueries[^1]);
        Assert.Equal("true", page.FindAll(".chip").Single(b => b.TextContent == "24 hours").GetAttribute("aria-pressed"));
    }

    [Fact]
    public void Live_samples_of_this_server_are_appended_and_other_servers_are_ignored()
    {
        AddServer(1, "web-01", TimeSpan.Zero);
        Api.Metrics.Add(Sample(1, Now.AddMinutes(-1), cpu: 20));
        var page = Render<ServerDetail>(ps => ps.Add(p => p.ServerId, 1));

        Feed.Push(new MetricsReceivedEvent(2, "db-01", [Sample(2, Now, cpu: 99)], Now));
        Feed.Push(new MetricsReceivedEvent(1, "web-01", [Sample(1, Now, cpu: 45)], Now));

        page.WaitForAssertion(() => Assert.Contains("45 %", page.FindAll(".chart")[0].TextContent, StringComparison.Ordinal));
        Assert.DoesNotContain("99 %", page.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void New_logs_of_this_server_reload_the_recent_list_at_most_once_per_interval()
    {
        AddServer(1, "web-01", TimeSpan.Zero);
        var page = Render<ServerDetail>(ps => ps.Add(p => p.ServerId, 1));
        var queries = Api.LogQueries.Count;

        Api.Logs.Add(Log(1, 1, LogSeverity.Warning, "slow request", Now));
        Feed.Push(new LogsReceivedEvent(1, "web-01", 1, LogSeverity.Warning, Now));
        page.WaitForAssertion(() => Assert.Contains("slow request", page.Markup, StringComparison.Ordinal));
        Feed.Push(new LogsReceivedEvent(1, "web-01", 1, LogSeverity.Warning, Now));
        Feed.Push(new LogsReceivedEvent(1, "web-01", 1, LogSeverity.Warning, Now));

        Assert.Equal(queries + 1, Api.LogQueries.Count);
        Time.Advance(Settings.LiveRefreshInterval);
        page.WaitForAssertion(() => Assert.Equal(queries + 2, Api.LogQueries.Count));
    }
}

public class LogsPageTests : DashboardPageTests
{
    private IRenderedComponent<Logs> RenderAt(string url)
    {
        Navigation.NavigateTo(url);
        return Render<Logs>();
    }

    [Fact]
    public void The_filters_come_from_the_address_so_views_can_be_shared()
    {
        AddServer(2, "web-02", TimeSpan.Zero);

        var page = RenderAt("logs?serverId=2&minSeverity=warning&search=%20timeout%20&from=2026-01-01T10:00&page=3");

        var query = Assert.Single(Api.LogQueries);
        Assert.Equal(2, query.ServerId);
        Assert.Equal(LogSeverity.Warning, query.MinSeverity);
        Assert.Equal("timeout", query.Search);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero), query.From);
        Assert.Null(query.To);
        Assert.Equal((3, Logs.PageSize), (query.Page, query.PageSize));
        Assert.Equal("2", page.Find("select").GetAttribute("value"));
    }

    [Fact]
    public void Nonsense_in_the_address_is_ignored()
    {
        RenderAt("logs?minSeverity=7&from=yesterday&page=-4");

        var query = Assert.Single(Api.LogQueries);
        Assert.Equal((null, null, 1), (query.MinSeverity, query.From, query.Page));
    }

    [Fact]
    public void Applying_the_filters_puts_them_in_the_address()
    {
        AddServer(2, "web-02", TimeSpan.Zero);
        var page = RenderAt("logs?page=4");

        page.FindAll("select")[0].Change("2");
        page.FindAll("select")[1].Change("Error");
        page.Find("input[type=search]").Change("disk full");
        page.Find("form").Submit();

        Assert.Equal("http://localhost/logs?serverId=2&minSeverity=Error&search=disk%20full", Navigation.Uri);
    }

    [Fact]
    public void Dates_typed_in_the_form_reach_the_address_and_to_covers_its_whole_minute()
    {
        var page = RenderAt("logs");

        page.FindAll("input[type=datetime-local]")[0].Change("2026-01-01T10:00");
        page.FindAll("input[type=datetime-local]")[1].Change("2026-01-01T10:30");
        page.Find("form").Submit();

        Assert.Equal("http://localhost/logs?from=2026-01-01T10%3A00&to=2026-01-01T10%3A30", Navigation.Uri);
        var query = Api.LogQueries[^1];
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero), query.From);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 10, 31, 0, TimeSpan.Zero).AddTicks(-1), query.To);
        Assert.Equal("2026-01-01T10:30", page.FindAll("input[type=datetime-local]")[1].GetAttribute("value"));
    }

    [Fact]
    public void Dates_in_the_wrong_order_are_refused_before_navigating()
    {
        var page = RenderAt("logs");

        page.FindAll("input[type=datetime-local]")[0].Change("2026-01-02T00:00");
        page.FindAll("input[type=datetime-local]")[1].Change("2026-01-01T00:00");
        page.Find("form").Submit();

        Assert.Equal("http://localhost/logs", Navigation.Uri);
        Assert.Contains("'From' must be earlier than 'To'", page.Find(".notice-error").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void A_page_past_the_last_one_offers_the_last_page()
    {
        AddServer(1, "web-01", TimeSpan.Zero);
        for (var i = 0; i < 60; i++)
        {
            Api.Logs.Add(Log(i + 1, 1, LogSeverity.Information, $"line {i}", Now.AddSeconds(-i)));
        }

        var page = RenderAt("logs?page=50");

        Assert.Contains("This page is past the last one", page.Markup, StringComparison.Ordinal);
        Assert.Contains("60 entries", page.Find(".page-header").TextContent, StringComparison.Ordinal);
        page.Find(".notice button").Click();
        Assert.Equal("http://localhost/logs?page=2", Navigation.Uri);
    }

    [Fact]
    public void A_new_server_does_not_hide_a_filter_error()
    {
        var page = RenderAt("logs?from=2026-01-02T00:00&to=2026-01-01T00:00");

        AddServer(9, "new-01", TimeSpan.Zero);
        Feed.Push(new LogsReceivedEvent(9, "new-01", 1, LogSeverity.Error, Now));

        page.WaitForAssertion(() => Assert.True(Api.ServerCalls >= 2));
        Assert.Contains("'From' must be earlier than 'To'", page.Find(".notice-error").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Entries_are_listed_with_their_server_and_severity()
    {
        AddServer(1, "web-01", TimeSpan.Zero);
        Api.Logs.Add(Log(1, 1, LogSeverity.Critical, "service down", Now));

        var page = RenderAt("logs");

        var row = page.Find("table.logs tbody tr");
        Assert.Contains("web-01", row.TextContent, StringComparison.Ordinal);
        Assert.Equal("Critical", row.QuerySelector(".severity")!.TextContent);
        Assert.Contains("2026-01-01 12:00:00", row.TextContent, StringComparison.Ordinal);
        Assert.Contains("1–1 of 1 entries", page.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Following_live_reloads_the_first_page_without_hammering_the_api()
    {
        AddServer(1, "web-01", TimeSpan.Zero);
        var page = RenderAt("logs");
        Assert.Single(Api.LogQueries);

        Api.Logs.Add(Log(1, 1, LogSeverity.Information, "hello", Now));
        Feed.Push(new LogsReceivedEvent(1, "web-01", 1, LogSeverity.Information, Now));
        page.WaitForAssertion(() => Assert.Contains("hello", page.Markup, StringComparison.Ordinal));
        Feed.Push(new LogsReceivedEvent(1, "web-01", 1, LogSeverity.Information, Now));
        Feed.Push(new LogsReceivedEvent(1, "web-01", 1, LogSeverity.Information, Now));

        Assert.Equal(2, Api.LogQueries.Count);
        Time.Advance(Settings.LiveRefreshInterval);
        page.WaitForAssertion(() => Assert.Equal(3, Api.LogQueries.Count));
    }

    [Fact]
    public void Batches_that_cannot_match_the_filters_are_ignored()
    {
        RenderAt("logs?serverId=1&minSeverity=Error");

        Feed.Push(new LogsReceivedEvent(2, "db-01", 5, LogSeverity.Critical, Now));
        Feed.Push(new LogsReceivedEvent(1, "web-01", 5, LogSeverity.Warning, Now));

        Assert.Single(Api.LogQueries);
    }

    [Fact]
    public void Away_from_the_newest_entries_new_ones_are_announced_instead_of_moving_the_list()
    {
        AddServer(1, "web-01", TimeSpan.Zero);
        var page = RenderAt("logs?page=2");
        Assert.True(page.Find(".toggle input").HasAttribute("disabled"));

        Feed.Push(new LogsReceivedEvent(1, "web-01", 1, LogSeverity.Information, Now));

        page.WaitForAssertion(() => Assert.Contains("New entries have arrived", page.Markup, StringComparison.Ordinal));
        Assert.Single(Api.LogQueries);
        page.Find(".notice-inline button").Click();
        Assert.Equal("http://localhost/logs", Navigation.Uri);
    }

    [Fact]
    public void Turning_follow_off_announces_new_entries_on_the_first_page_too()
    {
        AddServer(1, "web-01", TimeSpan.Zero);
        var page = RenderAt("logs");

        page.Find(".toggle input").Change(false);
        Feed.Push(new LogsReceivedEvent(1, "web-01", 1, LogSeverity.Information, Now));

        page.WaitForAssertion(() => Assert.Contains("New entries have arrived", page.Markup, StringComparison.Ordinal));
        page.Find(".notice-inline button").Click();
        Assert.Equal(2, Api.LogQueries.Count);
        Assert.DoesNotContain("New entries have arrived", page.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void A_from_date_after_the_to_date_is_explained_instead_of_queried()
    {
        var page = RenderAt("logs?from=2026-01-02T00:00&to=2026-01-01T00:00");

        Assert.Empty(Api.LogQueries);
        Assert.Contains("'From' must be earlier than 'To'", page.Find(".notice-error").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void The_pager_moves_through_older_entries()
    {
        AddServer(1, "web-01", TimeSpan.Zero);
        for (var i = 0; i < 120; i++)
        {
            Api.Logs.Add(Log(i + 1, 1, LogSeverity.Information, $"line {i}", Now.AddSeconds(-i)));
        }

        var page = RenderAt("logs");

        Assert.Contains("Page 1 of 3", page.Find(".pager").TextContent, StringComparison.Ordinal);
        Assert.True(page.FindAll(".pager button")[0].HasAttribute("disabled"));
        page.FindAll(".pager button")[1].Click();
        Assert.Equal("http://localhost/logs?page=2", Navigation.Uri);
    }
}

public class SharedComponentTests : BunitContext
{
    private static readonly DateTimeOffset From = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_chart_draws_its_points_and_reports_the_latest_and_peak_values()
    {
        var chart = Render<LineChart>(ps => ps
            .Add(p => p.Title, "CPU")
            .Add(p => p.Points, [new ChartPoint(From.AddMinutes(1), 30), new ChartPoint(From.AddMinutes(2), 80), new ChartPoint(From.AddMinutes(3), 40)])
            .Add(p => p.From, From)
            .Add(p => p.To, From.AddMinutes(10)));

        Assert.Single(chart.FindAll("polyline"));
        Assert.Equal("40 %", chart.Find(".chart-latest").TextContent);
        Assert.Equal("peak 80 %", chart.Find(".chart-range").TextContent);
        Assert.Equal("CPU: 40 % now, peak 80 %", chart.Find("svg").GetAttribute("aria-label"));
    }

    [Fact]
    public void Day_long_charts_label_the_axis_with_dates()
    {
        var chart = Render<LineChart>(ps => ps
            .Add(p => p.Title, "CPU")
            .Add(p => p.Points, [])
            .Add(p => p.From, From)
            .Add(p => p.To, From.AddDays(1)));

        Assert.Equal(["01-01 12:00", "01-02 12:00 UTC"], chart.FindAll(".chart-axis span").Where(s => s.ClassName != "chart-empty").Select(s => s.TextContent));
    }

    [Fact]
    public void An_empty_chart_says_there_is_no_data()
    {
        var chart = Render<LineChart>(ps => ps
            .Add(p => p.Title, "Disk")
            .Add(p => p.Points, [])
            .Add(p => p.From, From)
            .Add(p => p.To, From.AddHours(1)));

        Assert.Empty(chart.FindAll("polyline"));
        Assert.Contains("No samples in this period", chart.Markup, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(LiveStatus.Live, "Live")]
    [InlineData(LiveStatus.Reconnecting, "Reconnecting…")]
    [InlineData(LiveStatus.Offline, "Live updates offline, retrying")]
    public void The_live_indicator_names_the_connection_state(LiveStatus status, string text)
    {
        var indicator = Render<LiveIndicator>(ps => ps.Add(p => p.Status, status));

        Assert.Equal(text, indicator.Find(".live").TextContent.Trim());
        Assert.Contains($"live-{status.ToString().ToLowerInvariant()}", indicator.Find(".live").ClassName, StringComparison.Ordinal);
    }

    [Fact]
    public void A_usage_bar_is_an_accessible_meter_capped_at_100_percent()
    {
        var bar = Render<UsageBar>(ps => ps.Add(p => p.Label, "Disk").Add(p => p.Percent, 120));

        Assert.Equal("120", bar.Find("[role=meter]").GetAttribute("aria-valuenow"));
        Assert.Contains("width: 100%", bar.Find(".usage-fill").GetAttribute("style"), StringComparison.Ordinal);
    }
}
