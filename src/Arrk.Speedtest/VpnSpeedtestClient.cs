using System.Net;
using Arrk.Core;
using Arrk.Core.Services;

namespace Arrk.Speedtest;

/// <summary>
/// Builds an <see cref="HttpClient"/> whose traffic is routed through the VPN (Gluetun)
/// container's built-in HTTP proxy. This lets the same <see cref="CloudflareSpeedtestClient"/>
/// measure the VPN connection "identically" to the host network, the only difference is the
/// proxy. The proxy host is resolved from <see cref="ArrkInstance"/> so it follows the
/// running stack name.
/// </summary>
public class VpnSpeedtestClient(ArrkInstance arrk)
{
    /// <summary>Address of Gluetun's HTTP proxy as seen from Arrk's own network.</summary>
    public virtual string ProxyAddress =>
        $"http://{arrk.GetVpnContainerName()}:{GluetunServiceDefinition.HttpProxyPort}";

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
