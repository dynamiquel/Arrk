using Microsoft.Extensions.Configuration;
using Arrk.Core;
using Arrk.Core.Configuration;
using Arrk.Core.Models;
using Arrk.Core.Services;

namespace Arrk.Transmission;

public class TransmissionServiceDefinition : IServiceDefinition
{
    public const string StaticKey = "torrent_downloader";
    public const string DesiredUsername = "transmission";
    public const string DesiredPassword = "stackarr";

    public string Key => StaticKey;
    public string DisplayName => "Downloads";
    public string Description => "Torrent download client";
    public bool IsInfrastructure => true;
    public IReadOnlyList<string> ProvisionBefore => [];
    public IReadOnlyList<string> ProvisionAfter => ["vpn"];
    public string ServiceKey => StaticKey;

    public bool IsEnabled(IConfiguration configuration) => true;

    public IReadOnlyList<ContainerSpec> BuildContainerSpecs(
        IConfiguration configuration,
        VolumePathResolver volumeResolver,
        ArrkInstance arrk)
    {
        Dictionary<string, string> env = new Dictionary<string, string>(ArrkServiceExtensions.GetDefaultEnvironment())
        {
            //["TRANSMISSION_WEB_HOME"] = "/transmission",
            ["USER"] = DesiredUsername,
            ["PASS"] = DesiredPassword,
            // Internal listener only. Arrk does not publish a peer port: behind NAT/tunnels
            // peers cannot reach it, and an unused host bind collides across stacks.
            ["PEERPORT"] = "51413",
            ["RPC_PORT"] = "9091",
            ["RPC_WHITELIST_ENABLED"] = "false",
            ["RPC_AUTHENTICATION_REQUIRED"] = "true",
            ["RPC_USERNAME"] = DesiredUsername,
            ["RPC_PASSWORD"] = DesiredPassword
        };

        List<VolumeMount> volumes = new List<VolumeMount>
        {
            new()
            {
                HostPath = $"{arrk.ContainerPrefix}transmission-config",
                ContainerPath = "/config",
                Type = VolumeType.NamedVolume,
                Permissions = VolumePerms.ReadWrite
            },
            new()
            {
                HostPath = RelativeVolumes.Downloads,
                ContainerPath = "/downloads",
                Type = VolumeType.Bind,
                Permissions = VolumePerms.ReadWrite
            }
        };

        bool vpnRouted = configuration.IsTorrentVpnRouted();
        string? networkMode = vpnRouted ? arrk.GetVpnNetworkMode() : null;
        bool proxy = configuration.IsServiceProxied("services:torrentDownloader", defaultValue: false);

        return
        [
            new()
            {
                Image = "lscr.io/linuxserver/transmission:latest",
                Name = arrk.GetContainerName("transmission"),
                Environment = env,
                Volumes = volumes,
                Labels = new()
                {
                    [ContainerLabels.Service] = "torrent_downloader"
                },
                NetworkName = arrk.NetworkName,
                RestartPolicy = RestartPolicyType.UnlessStopped,
                NetworkMode = networkMode,
                // Traefik metadata is always populated so the provisioner can decide whether to
                // emit Docker labels (normal) or a file-provider route pointing at Gluetun (VPN).
                Traefik = proxy ? new TraefikConfig
                {
                    ServiceName = "transmission",
                    Rule = "PathPrefix(`/transmission`)",
                    Port = 9091
                } : null
            }
        ];
    }
}
