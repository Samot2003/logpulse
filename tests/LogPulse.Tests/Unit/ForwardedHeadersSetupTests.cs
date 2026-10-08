using System.Net;
using LogPulse.Api.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using DashboardForwardedHeadersSetup = LogPulse.Dashboard.Options.ForwardedHeadersSetup;

namespace LogPulse.Tests.Unit;

public class ForwardedHeadersSetupTests
{
    // The Azure chain: user -> ingress -> dashboard -> ingress -> API, with the ingress and the dashboard in 10.40.0.0/27.
    private static readonly Dictionary<string, string?> AzureApiSettings = new()
    {
        ["ForwardedHeaders:KnownNetworks:0"] = "10.40.0.0/27",
        ["ForwardedHeaders:ForwardLimit"] = "2",
    };

    [Fact]
    public void Reads_proxies_networks_and_limit_including_their_IPv4_mapped_form()
    {
        var options = Configure(new()
        {
            ["ForwardedHeaders:KnownProxies:0"] = "172.30.88.10",
            ["ForwardedHeaders:KnownNetworks:0"] = "10.40.0.0/27",
            ["ForwardedHeaders:ForwardLimit"] = "2",
        });

        Assert.Contains(IPAddress.Parse("172.30.88.10"), options.KnownProxies);
        Assert.Contains(IPAddress.Parse("::ffff:172.30.88.10"), options.KnownProxies);
        Assert.Contains(options.KnownNetworks, n => n.Contains(IPAddress.Parse("10.40.0.20")));
        Assert.Contains(options.KnownNetworks, n => n.Contains(IPAddress.Parse("::ffff:10.40.0.20")));
        Assert.DoesNotContain(options.KnownNetworks, n => n.Contains(IPAddress.Parse("10.40.0.40")));
        Assert.Equal(2, options.ForwardLimit);
    }

    [Fact]
    public void Without_settings_only_the_defaults_remain()
    {
        var defaults = new ForwardedHeadersOptions();
        var options = Configure([]);

        Assert.Equal(defaults.KnownProxies, options.KnownProxies);
        Assert.Equal(defaults.KnownNetworks.Count, options.KnownNetworks.Count);
        Assert.Equal(1, options.ForwardLimit);
    }

    [Fact]
    public void A_forward_limit_below_one_is_a_configuration_error() =>
        Assert.Throws<InvalidOperationException>(() => Configure(new() { ["ForwardedHeaders:ForwardLimit"] = "0" }));

    [Fact]
    public void The_dashboard_reads_the_same_section()
    {
        var options = new ForwardedHeadersOptions();
        DashboardForwardedHeadersSetup.Apply(options, Build(AzureApiSettings));

        Assert.Contains(options.KnownNetworks, n => n.Contains(IPAddress.Parse("::ffff:10.40.0.20")));
        Assert.Equal(2, options.ForwardLimit);
    }

    [Fact]
    public async Task Behind_the_ingress_the_API_sees_the_user_that_the_dashboard_forwarded()
    {
        // The dashboard sent "X-Forwarded-For: user"; the ingress appended the dashboard's own address.
        var remote = await ClientAddressAsync("203.0.113.7, 10.40.0.9", ingress: "::ffff:10.40.0.4");
        Assert.Equal(IPAddress.Parse("203.0.113.7"), remote);
    }

    [Fact]
    public async Task A_caller_on_the_internet_cannot_spoof_its_address()
    {
        // The attacker sends "X-Forwarded-For: 198.51.100.1"; the ingress appends the attacker's real address.
        var remote = await ClientAddressAsync("198.51.100.1, 192.0.2.66", ingress: "::ffff:10.40.0.4");
        Assert.Equal(IPAddress.Parse("192.0.2.66"), remote);
    }

    [Fact]
    public async Task A_peer_outside_the_known_networks_is_not_trusted()
    {
        var remote = await ClientAddressAsync("203.0.113.7", ingress: "192.0.2.66");
        Assert.Equal(IPAddress.Parse("192.0.2.66"), remote);
    }

    private static async Task<IPAddress?> ClientAddressAsync(string forwardedFor, string ingress)
    {
        var options = Configure(AzureApiSettings);
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
        var middleware = new ForwardedHeadersMiddleware(_ => Task.CompletedTask, NullLoggerFactory.Instance, Microsoft.Extensions.Options.Options.Create(options));

        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(ingress);
        context.Request.Headers["X-Forwarded-For"] = forwardedFor;
        await middleware.Invoke(context);
        return context.Connection.RemoteIpAddress?.IsIPv4MappedToIPv6 == true
            ? context.Connection.RemoteIpAddress.MapToIPv4()
            : context.Connection.RemoteIpAddress;
    }

    private static ForwardedHeadersOptions Configure(Dictionary<string, string?> settings)
    {
        var options = new ForwardedHeadersOptions();
        ForwardedHeadersSetup.Apply(options, Build(settings));
        return options;
    }

    private static IConfiguration Build(Dictionary<string, string?> settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
}
