using Microsoft.Extensions.Configuration;
using Stackarr.Core;
using Stackarr.Core.Configuration;
using Stackarr.Core.Models;
using Stackarr.Core.Services;

namespace Stackarr.Jellyfin;

public class JellyfinServiceDefinition : IServiceDefinition
{
    public const string StaticKey = "jellyfin";

    public string Key => StaticKey;
    public string DisplayName => "Jellyfin";
    public string Description => "Media player for movies, TV, and music";
    public bool IsInfrastructure => false;
    public IReadOnlyList<string> ProvisionBefore => [];
    public IReadOnlyList<string> ProvisionAfter => [];

    public bool IsEnabled(IConfiguration configuration) => configuration.IsServiceEnabled("services:jellyfin");

    public IReadOnlyList<ContainerSpec> BuildContainerSpecs(
        IConfiguration configuration,
        VolumePathResolver volumeResolver,
        StackarrInstance stackarr)
    {
        bool proxy = configuration.IsServiceProxied("services:jellyfin", defaultValue: true);

        Dictionary<string, string> env = new Dictionary<string, string>(StackarrServiceExtensions.GetDefaultEnvironment());
        Dictionary<string, string> labels = new Dictionary<string, string>
        {
            [ContainerLabels.Service] = "jellyfin"
        };

        List<string>? capabilities = null;
        string? runtime = null;

        if (configuration.GetValue<bool>("services:jellyfin:nvidia"))
        {
            env["NVIDIA_VISIBLE_DEVICES"] = "all";
            env["NVIDIA_DRIVER_CAPABILITIES"] = "compute,video,utility,3d";
            labels["com.nvidia.volumes.quirks"] = "udev";
            runtime = "nvidia";
            capabilities = ["SYS_ADMIN"];
        }

        // Jellyfin's Base URL is stored in network.xml rather than an environment variable, so
        // the config directory is bind-mounted and the file is updated before the container
        // starts. This is the officially documented way to host Jellyfin on a subpath.
        string configVolume = RelativeVolumes.Config + ":jellyfin";
        JellyfinNetworkConfig.SetBaseUrl(
            volumeResolver.ResolveToLocalPath(configVolume),
            proxy ? "/jellyfin" : null,
            defaultFileName: JellyfinNetworkConfig.NetworkFileName);

        return
        [
            new()
            {
                Image = "lscr.io/linuxserver/jellyfin:latest",
                Name = stackarr.GetContainerName("jellyfin"),
                Environment = env,
                Volumes =
                [
                    new()
                    {
                        HostPath = configVolume,
                        ContainerPath = "/config",
                        Type = VolumeType.Bind,
                        Permissions = VolumePerms.ReadWrite
                    },
                    new()
                    {
                        HostPath = RelativeVolumes.TvMovies,
                        ContainerPath = "/movies",
                        Type = VolumeType.Bind,
                    },
                    new()
                    {
                        HostPath = RelativeVolumes.TvShows,
                        ContainerPath = "/shows",
                        Type = VolumeType.Bind
                    },
                    new()
                    {
                        HostPath = RelativeVolumes.Music,
                        ContainerPath = "/music",
                        Type = VolumeType.Bind
                    }
                ],
                Labels = labels,
                NetworkName = stackarr.NetworkName,
                RestartPolicy = RestartPolicyType.UnlessStopped,
                Runtime = runtime,
                Capabilities = capabilities ?? [],
                Traefik = proxy ? new()
                {
                    ServiceName = "jellyfin",
                    Rule = "PathPrefix(`/jellyfin`)",
                    Port = 8096
                } : null
            }
        ];
    }
}
