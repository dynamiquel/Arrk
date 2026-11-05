using System.Net;
using Stackarr.Core;
using Stackarr.Core.Services;

namespace Stackarr.Speedtest;

/// <summary>
/// Builds an <see cref="HttpClient"/> whose traffic is routed through the VPN (Gluetun)
/// container's built-in HTTP proxy. This lets the same <see cref="CloudflareSpeedtestClient"/>
/// measure the VPN connection "identically" to the host network, the only difference is the
/// proxy. The proxy host is resolved from <see cref="StackarrInstance"/> so it follows the
/// running stack name.
/// </summary>
public class VpnSpeedtestClient(StackarrInstance stackarr)
{
    /// <summary>Address of Gluetun's HTTP proxy as seen from Stackarr's own network.</summary>
    public virtual string ProxyAddress =>
        $"http://{stackarr.GetVpnContainerName()}:{GluetunServiceDefinition.HttpProxyPort}";

    public virtual HttpClient CreateClient(TimeSpan? timeout = null)
    {
        return new HttpClient(CreateHandler())
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(120)
        };
    }

    public virtual SocketsHttpHandler CreateHandler() => new()
    {
        Proxy = new WebProxy(ProxyAddress),
        UseProxy = true,
        ConnectTimeout = TimeSpan.FromSeconds(15),
    };
}
