using Microsoft.Extensions.Configuration;
using Stackarr.Core.Configuration;
using Stackarr.Core.Models;

namespace Stackarr.Core.Services;

public class TraefikServiceDefinition : IServiceDefinition
{
    public const string StaticKey = "reverse_proxy";
    public const string Image = "traefik:v3.7";

    public string Key => StaticKey;
    public string DisplayName => "Proxy";
    public string Description => "Reverse proxy and SSL termination";
    public bool IsInfrastructure => true;
    public IReadOnlyList<string> ProvisionBefore => ["vpn"];
    public IReadOnlyList<string> ProvisionAfter => [];

    public bool IsEnabled(IConfiguration configuration) => true;

    /// <summary>
    /// Traefik constraint limiting Docker provider discovery to containers of this Stackarr
    /// instance. Without it every Traefik instance on the host discovers every other stack's
    /// containers, and identical service names (for example <c>jellyfin</c>) merge into one
    /// load balancer that sends half the requests to an unreachable network namespace.
    /// </summary>
    public static string BuildInstanceConstraint(StackarrInstance stackarr) =>
        $"--providers.docker.constraints=Label(`{ContainerLabels.InstanceId}`,`{stackarr.StackName}`)";

    public IReadOnlyList<ContainerSpec> BuildContainerSpecs(IConfiguration configuration, VolumePathResolver volumeResolver, StackarrInstance stackarr) =>
    [
        new()
        {
            Image = Image,
            Name = stackarr.GetContainerName("traefik"),
            Command =
            [
                "--providers.docker=true",
                "--providers.docker.exposedbydefault=false",
                $"--providers.docker.network={stackarr.NetworkName}",
                BuildInstanceConstraint(stackarr),
                "--providers.file.directory=/dynamic",
                "--providers.file.watch=true",
                "--entrypoints.web.address=:80",
                "--entrypoints.websecure.address=:443"
            ],
            Environment = new()
            {
                ["TZ"] = "Etc/UTC"
            },
            Volumes =
            [
                new()
                {
                    HostPath = RelativeVolumes.DockerSocket,
                    ContainerPath = "/var/run/docker.sock",
                    Type = VolumeType.Bind
                },
                new()
                {
                    HostPath = RelativeVolumes.ReverseProxyDynamic,
                    ContainerPath = "/dynamic",
                    Type = VolumeType.Bind
                }
            ],
            Ports = BuildPorts(configuration),
            Labels = new()
            {
                [ContainerLabels.Service] = "reverse_proxy"
            },
            NetworkName = stackarr.NetworkName,
            RestartPolicy = RestartPolicyType.UnlessStopped
        }
    ];

    /// <summary>
    /// The proxy's entrypoints always listen on 80/443 inside the container, but the host ports
    /// they are published on are configurable. Setting a port to 0 leaves it unpublished, which
    /// lets several instances share a host and keeps tunnel-only deployments from grabbing the
    /// host's 80/443.
    /// </summary>
    static List<PortMapping> BuildPorts(IConfiguration configuration)
    {
        RoutesConfig routes = configuration.GetSection(RoutesConfig.SectionName).Get<RoutesConfig>() ?? new RoutesConfig();

        List<PortMapping> ports = [];
        if (routes.HttpPort > 0)
            ports.Add(new() { HostPort = routes.HttpPort, ContainerPort = 80 });
        if (routes.HttpsPort > 0)
            ports.Add(new() { HostPort = routes.HttpsPort, ContainerPort = 443 });
        return ports;
    }
}
