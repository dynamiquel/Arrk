using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace Arrk.Core.Configuration;

public class VpnConfig
{
    [Description("Enable the VPN (Gluetun) tunnel. Services only route through it when they opt in")]
    public bool Enabled { get; set; }

    [Description("VPN provider name")]
    public string Provider { get; set; } = "protonvpn";

    [Description("VPN protocol")]
    [AllowedValues("wireguard", "openvpn")]
    public string Type { get; set; } = "wireguard";

    [Description("WireGuard private key (required when VPN enabled)")]
    [DataType(DataType.Password)]
    public string WireguardPrivateKey { get; set; } = "";

    [Description("WireGuard addresses in CIDR notation (required when VPN enabled)")]
    public string WireguardAddresses { get; set; } = "";

    [Description("Comma-separated server country codes")]
    public string ServerCountries { get; set; } = "";

    [Description("Ask the VPN provider for a forwarded port (e.g. ProtonVPN WireGuard port forwarding)")]
    public bool PortForwarding { get; set; }

    [Description("Additional host ports to publish on the VPN container")]
    public List<VpnPortConfig> Ports { get; set; } = new();

    [Description("Comma-separated subnets allowed to reach services sharing the VPN network namespace (e.g. the Docker network subnet)")]
    public string FirewallOutboundSubnets { get; set; } = "";

    [Description("Restart the tunnel when a VPN speed test is very low")]
    public VpnRestartConfig Restart { get; set; } = new();
}

public class VpnRestartConfig
{
    [Description("Enable restarting the VPN tunnel after a very low VPN speed test")]
    public bool Enabled { get; set; } = true;

    [Description("Download Mbps threshold below which the tunnel is restarted (0 disables)")]
    public double DownloadThreshold { get; set; } = 5.0;

    [Description("Upload Mbps threshold below which the tunnel is restarted (0 disables)")]
    public double UploadThreshold { get; set; } = 5.0;

    [Description("Minimum minutes between tunnel restarts (cooldown)")]
    public int CooldownMinutes { get; set; } = 60;
}

public class VpnPortConfig
{
    [Description("Host port to publish")]
    [Range(1, 65535)]
    public int Host { get; set; }

    [Description("Port inside the VPN network namespace")]
    [Range(1, 65535)]
    public int Container { get; set; }

    [Description("Protocol")]
    [AllowedValues("tcp", "udp")]
    public string Protocol { get; set; } = "tcp";
}
