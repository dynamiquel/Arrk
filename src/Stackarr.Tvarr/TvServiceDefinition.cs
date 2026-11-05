using Microsoft.Extensions.Configuration;
using Stackarr.Core;
using Stackarr.Core.Configuration;
using Stackarr.Core.Models;
using Stackarr.Core.Services;

namespace Stackarr.Tvarr;

public class TvServiceDefinition : IServiceDefinition
{
    public const string StaticKey = "tv";

    public string Key => StaticKey;
    public string DisplayName => "TV";
    public string Description => "Movie and TV show management";
    public bool IsInfrastructure => false;
    public IReadOnlyList<string> ProvisionBefore => [];
    public IReadOnlyList<string> ProvisionAfter => ["indexer", "torrent_downloader", "bazarr"];

    public bool IsEnabled(IConfiguration configuration) => configuration.IsServiceEnabled("services:tv");

    public IReadOnlyList<ContainerSpec> BuildContainerSpecs(
        IConfiguration configuration,
        VolumePathResolver volumeResolver,
        StackarrInstance stackarr)
    {
        Dictionary<string, string> radarrEnv = new Dictionary<string, string>(StackarrServiceExtensions.GetDefaultEnvironment())
        {
            ["RADARR__AUTH__APIKEY"] = TvModule.ServiceApiKey,
            ["RADARR__SERVER__URLBASE"] = "/radarr"
        };
        Dictionary<string, string> sonarrEnv = new Dictionary<string, string>(StackarrServiceExtensions.GetDefaultEnvironment())
        {
            ["SONARR__AUTH__APIKEY"] = TvModule.ServiceApiKey,
            ["SONARR__SERVER__URLBASE"] = "/sonarr"
        };

        bool proxy = configuration.IsServiceProxied("services:tv", defaultValue: false);

        return
        [
            new()
            {
                Image = "lscr.io/linuxserver/radarr:latest",
                Name = stackarr.GetContainerName("radarr"),
                Environment = radarrEnv,
                Volumes =
                [
                    new()
                    {
                        HostPath = $"{stackarr.ContainerPrefix}radarr-config",
                        ContainerPath = "/config",
                        Type = VolumeType.NamedVolume,
                        Permissions = VolumePerms.ReadWrite
                    },
                    new()
                    {
                        HostPath = RelativeVolumes.TvMovies,
                        ContainerPath = "/movies",
                        Type = VolumeType.Bind,
                        Permissions = VolumePerms.ReadWrite
                    },
                    new()
                    {
                        HostPath = RelativeVolumes.Downloads,
                        ContainerPath = "/downloads",
                        Type = VolumeType.Bind
                    }
                ],
                Labels = new()
                {
                    [ContainerLabels.Service] = "tv"
                },
                NetworkName = stackarr.NetworkName,
                RestartPolicy = RestartPolicyType.UnlessStopped,
                Traefik = proxy ? new()
                {
                    ServiceName = "radarr",
                    Rule = "PathPrefix(`/radarr`)",
                    Port = 7878
                } : null
            },
            new()
            {
                Image = "lscr.io/linuxserver/sonarr:latest",
                Name = stackarr.GetContainerName("sonarr"),
                Environment = sonarrEnv,
                Volumes =
                [
                    new()
                    {
                        HostPath = $"{stackarr.ContainerPrefix}sonarr-config",
                        ContainerPath = "/config",
                        Type = VolumeType.NamedVolume,
                        Permissions = VolumePerms.ReadWrite
                    },
                    new()
                    {
                        HostPath = RelativeVolumes.TvShows,
                        ContainerPath = "/shows",
                        Type = VolumeType.Bind,
                        Permissions = VolumePerms.ReadWrite
                    },
                    new()
                    {
                        HostPath = RelativeVolumes.Downloads,
                        ContainerPath = "/downloads",
                        Type = VolumeType.Bind
                    }
                ],
                Labels = new()
                {
                    [ContainerLabels.Service] = "tv"
                },
                NetworkName = stackarr.NetworkName,
                RestartPolicy = RestartPolicyType.UnlessStopped,
                Traefik = proxy ? new()
                {
                    ServiceName = "sonarr",
                    Rule = "PathPrefix(`/sonarr`)",
                    Port = 8989
                } : null
            }
        ];
    }
}
