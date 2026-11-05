using Microsoft.Extensions.Configuration;
using Stackarr.Core;
using Stackarr.Core.Configuration;
using Stackarr.Core.Models;
using Stackarr.Core.Services;

namespace Stackarr.Bazarr;

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

    public IReadOnlyList<ContainerSpec> BuildContainerSpecs(IConfiguration configuration, VolumePathResolver volumeResolver, StackarrInstance stackarr)
    {
        bool proxy = configuration.IsServiceProxied("services:subtitles:bazarr", defaultValue: false);

        return
        [
            new()
            {
                Image = "lscr.io/linuxserver/bazarr:latest",
                Name = stackarr.GetContainerName("bazarr"),
                Environment = new(StackarrServiceExtensions.GetDefaultEnvironment()),
                Volumes =
                [
                    new()
                    {
                        HostPath = $"{stackarr.ContainerPrefix}bazarr-config",
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
                NetworkName = stackarr.NetworkName,
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
