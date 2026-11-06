using Microsoft.Extensions.Configuration;
using Arrk.Core;
using Arrk.Core.Configuration;
using Arrk.Core.Models;
using Arrk.Core.Services;

namespace Arrk.Navidrome;

public class NavidromeServiceDefinition : IServiceDefinition
{
    public const string StaticKey = "navidrome";

    public string Key => StaticKey;
    public string DisplayName => "Navidrome";
    public string Description => "Music player and streaming";
    public bool IsInfrastructure => false;
    public IReadOnlyList<string> ProvisionBefore => [];
    public IReadOnlyList<string> ProvisionAfter => ["indexer", "torrent_downloader", "bazarr"];

    public bool IsEnabled(IConfiguration configuration) => configuration.IsServiceEnabled("services:navidrome");

    public IReadOnlyList<ContainerSpec> BuildContainerSpecs(IConfiguration configuration, VolumePathResolver volumeResolver, ArrkInstance arrk)
    {
        bool proxy = configuration.IsServiceProxied("services:navidrome", defaultValue: true);

        Dictionary<string, string> env = new(ArrkServiceExtensions.GetDefaultEnvironment())
        {
            ["ND_MUSICFOLDER"] = "/music",
            ["ND_DATAFOLDER"] = "/data"
        };

        if (proxy)
        {
            env["ND_BASEURL"] = "/navidrome";
        }

        return
        [
            new()
            {
                Image = "deluan/navidrome:latest",
                Name = arrk.GetContainerName("navidrome"),
                Environment = env,
                Volumes =
                [
                    new()
                    {
                        // Navidrome keeps its database, config, cache and plugins under DataFolder
                        // (ND_DATAFOLDER, /data). This MUST be the persistent volume: mounting it at
                        // /config instead left the real data on an anonymous volume that was
                        // discarded whenever the container was recreated.
                        HostPath = $"{arrk.ContainerPrefix}navidrome-data",
                        ContainerPath = "/data",
                        Type = VolumeType.NamedVolume,
                        Permissions = VolumePerms.ReadWrite
                    },
                    new()
                    {
                        HostPath = RelativeVolumes.Music,
                        ContainerPath = "/music",
                        Type = VolumeType.Bind,
                    }
                ],
                Labels = new()
                {
                    [ContainerLabels.Service] = "navidrome"
                },
                NetworkName = arrk.NetworkName,
                RestartPolicy = RestartPolicyType.UnlessStopped,
                Traefik = proxy ? new()
                {
                    ServiceName = "navidrome",
                    Rule = "PathPrefix(`/navidrome`)",
                    Port = 4533
                } : null
            }
        ];
    }
}
