using Microsoft.Extensions.Configuration;
using Stackarr.Core;
using Stackarr.Core.Configuration;
using Stackarr.Core.Models;
using Stackarr.Core.Services;
using Stackarr.Prowlarr.Configuration;

namespace Stackarr.Prowlarr;

public class ProwlarrServiceDefinition : IServiceDefinition
{
    public const string StaticKey = "indexer";

    /// <summary>Container name (implementation key) for the optional Cloudflare/DDoS solver.</summary>
    public const string SolverImplementation = "byparr";

    public string Key => StaticKey;
    public string DisplayName => "Indexer";
    public string Description => "Centralized indexer management";
    public bool IsInfrastructure => true;
    public IReadOnlyList<string> ProvisionBefore => ["tv", "music"];
    public IReadOnlyList<string> ProvisionAfter => [];

    public bool IsEnabled(IConfiguration configuration) => true;

    public IReadOnlyList<ContainerSpec> BuildContainerSpecs(IConfiguration configuration, VolumePathResolver volumeResolver, StackarrInstance stackarr)
    {
        bool proxy = configuration.IsServiceProxied("indexers", defaultValue: false);

        Dictionary<string, string> env = new(StackarrServiceExtensions.GetDefaultEnvironment())
        {
            ["PROWLARR__AUTH__APIKEY"] = ProwlarrModule.ServiceApiKey,
            ["PROWLARR__SERVER__URLBASE"] = "/prowlarr"
        };

        List<ContainerSpec> specs =
        [
            new()
            {
                Image = "lscr.io/linuxserver/prowlarr:latest",
                Name = stackarr.GetContainerName("prowlarr"),
                Environment = env,
                Volumes =
                [
                    new()
                    {
                        HostPath = $"{stackarr.ContainerPrefix}prowlarr-config",
                        ContainerPath = "/config",
                        Type = VolumeType.NamedVolume,
                        Permissions = VolumePerms.ReadWrite
                    }
                ],
                Labels = new()
                {
                    [ContainerLabels.Service] = StaticKey
                },
                NetworkName = stackarr.NetworkName,
                RestartPolicy = RestartPolicyType.UnlessStopped,
                Traefik = proxy ? new TraefikConfig
                {
                    ServiceName = "prowlarr",
                    Rule = "PathPrefix(`/prowlarr`)",
                    Port = 9696
                } : null
            }
        ];

        SolverConfig solver = configuration
            .GetSection("indexers")
            .Get<ProwlarrConfig>()?.Solver ?? new SolverConfig();

        if (solver.Enabled)
        {
            // The solver is a sibling container of Prowlarr, not a service of its own: it exists
            // only to answer Cloudflare/DDoS challenges on Prowlarr's behalf. It joins Stackarr's
            // network and is never published to the host, only Prowlarr needs to reach it.
            specs.Add(new ContainerSpec
            {
                Image = solver.ResolveImage(),
                Name = stackarr.GetContainerName(SolverImplementation),
                Environment = new()
                {
                    ["TZ"] = "Etc/UTC"
                },
                Labels = new()
                {
                    [ContainerLabels.Service] = StaticKey
                },
                NetworkName = stackarr.NetworkName,
                RestartPolicy = RestartPolicyType.UnlessStopped
            });
        }

        return specs;
    }
}
