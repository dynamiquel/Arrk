using Microsoft.Extensions.Configuration;
using Arrk.Core.Configuration;
using Arrk.Core.Models;

namespace Arrk.Core.Services;

public class GluetunServiceDefinition : IServiceDefinition
{
    public const string StaticKey = "vpn";

    /// <summary>Gluetun's built-in HTTP proxy port, used to run the VPN speed test through the tunnel.</summary>
    public const int HttpProxyPort = 8888;

    /// <summary>Gluetun's control-server port, used to restart the tunnel in place without a container restart.</summary>
    public const int ControlServerPort = 8000;

    public string Key => StaticKey;
    public string DisplayName => "VPN";
    public string Description => "VPN tunnel for privacy";
    public bool IsInfrastructure => false;
    public IReadOnlyList<string> ProvisionBefore => ["torrent_downloader"];
    public IReadOnlyList<string> ProvisionAfter => ["reverse_proxy"];

    public bool IsEnabled(IConfiguration configuration) => configuration.IsVpnTunnelRequired();

    public IReadOnlyList<ContainerSpec> BuildContainerSpecs(
        IConfiguration configuration, VolumePathResolver volumeResolver, ArrkInstance arrk)
    {
        VpnConfig vpn = configuration.GetSection("vpn").Get<VpnConfig>() ?? new VpnConfig();

        Dictionary<string, string> env = new()
        {
            ["VPN_SERVICE_PROVIDER"] = vpn.Provider,
            ["VPN_TYPE"] = vpn.Type,
            ["TZ"] = "Etc/UTC"
        };

        if (vpn.Type.Equals("wireguard", StringComparison.OrdinalIgnoreCase))
        {
            env["WIREGUARD_PRIVATE_KEY"] = vpn.WireguardPrivateKey;
            env["WIREGUARD_ADDRESSES"] = vpn.WireguardAddresses;
        }
        else
        {
            env["OPENVPN_USER"] = vpn.WireguardPrivateKey;
            env["OPENVPN_PASSWORD"] = vpn.WireguardAddresses;
        }

        if (!string.IsNullOrEmpty(vpn.ServerCountries))
            env["SERVER_COUNTRIES"] = vpn.ServerCountries;

        if (vpn.PortForwarding)
            env["VPN_PORT_FORWARDING"] = "on";

        // ProtonVPN and PIA expose whether a server is P2P/port-forwarding capable. Pick only
        // those servers when torrent traffic is routed: a random pick can otherwise land on a
        // non-P2P server where peer connections are blocked, which looks like torrents stuck at 0.
        if (configuration.IsTorrentVpnRouted() && SupportsPortForwardOnly(vpn.Provider))
            env["PORT_FORWARD_ONLY"] = "on";

        if (!string.IsNullOrEmpty(vpn.FirewallOutboundSubnets))
            env["FIREWALL_OUTBOUND_SUBNETS"] = vpn.FirewallOutboundSubnets;

        // Expose Gluetun's HTTP proxy so Arrk can run the VPN speed test through the tunnel.
        // Enabled whenever the tunnel runs (the proxy is only reachable on the internal Docker
        // network, not published), so the VPN is always measured.
        env["HTTPPROXY"] = "on";
        env["HTTPPROXY_PORT"] = HttpProxyPort.ToString();
        env["HTTPPROXY_LOG"] = "off";

        // Expose the control server so Arrk can restart the tunnel in place (stopped ->
        // running) to force a fresh provider reconnection. Internal network only, never
        // published to the host, so a static API key is sufficient.
        env["HTTP_CONTROL_SERVER_ADDRESS"] = $":{ControlServerPort}";
        env["HTTP_CONTROL_SERVER_AUTH_DEFAULT_ROLE"] =
            $@"{{""auth"":""apikey"",""apikey"":""{GluetunModule.ControlApiKey}""}}";

        return
        [
            new()
            {
                Image = "qmcgaw/gluetun:latest",
                Name = arrk.GetVpnContainerName(),
                Environment = env,
                Ports = BuildPorts(vpn),
                Labels = new()
                {
                    [ContainerLabels.Service] = "vpn"
                },
                NetworkName = arrk.NetworkName,
                RestartPolicy = RestartPolicyType.UnlessStopped,
                Capabilities = ["NET_ADMIN"],
                Devices = ["/dev/net/tun:/dev/net/tun"]
            }
        ];
    }

    static bool SupportsPortForwardOnly(string provider) =>
        provider.Equals("protonvpn", StringComparison.OrdinalIgnoreCase)
        || provider.Equals("private internet access", StringComparison.OrdinalIgnoreCase)
        || provider.Equals("pia", StringComparison.OrdinalIgnoreCase);

    static List<PortMapping> BuildPorts(VpnConfig vpn)
    {
        // Only explicitly configured extra ports are published. The torrent peer port is
        // Not published: Arrk deployments sit behind NAT/tunnels and peer
        // traffic is outbound-only, so a host bind would never be reachable and would just
        // collide with another stack on the same host.
        return vpn.Ports
            .Where(p => p.Host > 0 && p.Container > 0)
            .Select(p => new PortMapping
            {
                HostPort = p.Host,
                ContainerPort = p.Container,
                Protocol = string.IsNullOrWhiteSpace(p.Protocol) ? "tcp" : p.Protocol
            })
            .ToList();
    }
}
