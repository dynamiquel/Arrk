using Microsoft.Extensions.Configuration;
using Arrk.Core;
using Arrk.Core.Configuration;
using Arrk.Core.Models;
using Arrk.Core.Services;

namespace Arrk.Bazarr;

public class BazarrServiceDefinition : IServiceDefinition
{
    public const string StaticKey = "bazarr";

    public string Key => StaticKey;
    public string DisplayName => "Subtitles - Bazarr";
    public string Description => "Powerful subtitle finder and downloader";
    public bool IsInfrastructure => false;
    public IReadOnlyList<string> ProvisionBefore => [];
    public IReadOnlyList<string> ProvisionAfter => [];

    public bool IsEnabled(IConfiguration configuration) =>
        configuration.GetValue<bool?>("services:subtitles:bazarr:enabled") ?? false;

    public IReadOnlyList<ContainerSpec> BuildContainerSpecs(IConfiguration configuration, VolumePathResolver volumeResolver, ArrkInstance arrk)
    {
        bool proxy = configuration.IsServiceProxied("services:subtitles:bazarr", defaultValue: false);

        return
        [
            new()
            {
                Image = "lscr.io/linuxserver/bazarr:latest",
                Name = arrk.GetContainerName("bazarr"),
                Environment = new(ArrkServiceExtensions.GetDefaultEnvironment()),
                Volumes =
                [
                    new()
                    {
                        HostPath = $"{arrk.ContainerPrefix}bazarr-config",
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
                        HostPath = RelativeVolumes.TvShows,
                        ContainerPath = "/shows",
                        Type = VolumeType.Bind,
                        Permissions = VolumePerms.ReadWrite
                    }
                ],
                Labels = new()
                {
                    [ContainerLabels.Service] = StaticKey
                },
                NetworkName = arrk.NetworkName,
                RestartPolicy = RestartPolicyType.UnlessStopped,
                Traefik = proxy ? new()
                {
                    ServiceName = "bazarr",
                    Rule = "PathPrefix(`/bazarr`)",
                    Port = 6767,
                    StripPrefix = "/bazarr"
                } : null
            }
        ];
    }
}
