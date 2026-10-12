using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using IPNetwork = Microsoft.AspNetCore.HttpOverrides.IPNetwork;

namespace LogPulse.Dashboard.Options;

/// <summary>
/// Reads which proxies may set X-Forwarded-* headers from the "ForwardedHeaders" section: single addresses
/// (<c>KnownProxies</c>), CIDR ranges (<c>KnownNetworks</c>, for proxies without a fixed address such as the ingress
/// of Azure Container Apps) and how many proxy hops to unwrap (<c>ForwardLimit</c>, 1 by default).
/// </summary>
public static class ForwardedHeadersSetup
{
    public const string SectionName = "ForwardedHeaders";

    public static void Apply(ForwardedHeadersOptions options, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(SectionName);
        foreach (var proxy in section.GetSection("KnownProxies").Get<string[]>() ?? [])
        {
            var address = IPAddress.Parse(proxy);
            options.KnownProxies.Add(address);

            // Kestrel listens dual-stack in containers, so an IPv4 peer can show up as ::ffff:a.b.c.d.
            if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                options.KnownProxies.Add(address.MapToIPv6());
            }
        }

        foreach (var cidr in section.GetSection("KnownNetworks").Get<string[]>() ?? [])
        {
            var network = IPNetwork.Parse(cidr);
            options.KnownNetworks.Add(network);
            if (network.Prefix.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                options.KnownNetworks.Add(new IPNetwork(network.Prefix.MapToIPv6(), network.PrefixLength + 96));
            }
        }

        if (section.GetValue<int?>("ForwardLimit") is { } limit)
        {
            options.ForwardLimit = limit >= 1
                ? limit
                : throw new InvalidOperationException($"{SectionName}:ForwardLimit must be 1 or more.");
        }
    }
}
