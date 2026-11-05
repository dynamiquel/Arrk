using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace Stackarr.App.Services;

/// <summary>
/// Configures which peers may supply <c>X-Forwarded-*</c> headers. Stackarr listens on a
/// bootstrap port as well as behind its own proxy, so trusting every source would let a client
/// that reaches that port directly spoof its remote address, scheme and host. Only loopback and
/// the private ranges used by Docker/user-defined bridge networks are trusted by default.
/// </summary>
public static class ForwardedHeadersConfiguration
{
    static readonly string[] TrustedNetworks =
    [
        "10.0.0.0/8",
        "172.16.0.0/12",
        "192.168.0.0/16",
        "127.0.0.0/8",
        "fc00::/7",
        "fe80::/10",
        "::1/128"
    ];

    public static void Configure(ForwardedHeadersOptions options)
    {
        options.ForwardedHeaders =
            ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedHost | ForwardedHeaders.XForwardedProto;

        // A dual list forwards additions to the obsolete KnownNetworks property; clearing the
        // modern collection removes the framework defaults before adding the trusted ranges.
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();

        foreach (string network in TrustedNetworks)
            options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));

        options.KnownProxies.Add(IPAddress.Loopback);
        options.KnownProxies.Add(IPAddress.IPv6Loopback);
    }
}
