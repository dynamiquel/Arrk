using Microsoft.Extensions.Configuration;
using Arrk.Core;
using Arrk.Core.Configuration;
using Arrk.Core.Models;
using Arrk.Core.Services;

namespace Arrk.Cloudflared;

/// <summary>
/// Runs a Cloudflare Tunnel (<c>cloudflared</c>) that connects the public Cloudflare edge to the
/// Traefik reverse proxy over the internal Docker network. The tunnel is outbound-only, so no
/// host ports are exposed. Ingress (public hostname → origin) is managed in the Cloudflare
/// dashboard and is expected to point at Traefik (e.g. <c>http://arrk-dev-traefik:80</c>).
/// </summary>
public class CloudflaredServiceDefinition : IServiceDefinition
{
    public const string StaticKey = "cloudflared";
    public const string Image = "cloudflare/cloudflared:latest";

    public string Key => StaticKey;
    public string DisplayName => "Tunnel";
    public string Description => "Cloudflare tunnel for remote access without port forwarding";
    // Like the VPN, the tunnel is optional infrastructure: it is only provisioned when a
    // token is configured and must be stoppable when disabled, so it is not "always-on".
    public bool IsInfrastructure => false;

    // The tunnel's origin is Traefik, so it must be provisioned after the reverse proxy.
    public IReadOnlyList<string> ProvisionBefore => [];
    public IReadOnlyList<string> ProvisionAfter => ["reverse_proxy"];

    public bool IsEnabled(IConfiguration configuration)
    {
        CloudflaredConfig cloudflared = configuration
            .GetSection(CloudflaredConfig.SectionPath)
            .Get<CloudflaredConfig>() ?? new CloudflaredConfig();

        return cloudflared.Enabled && !string.IsNullOrWhiteSpace(cloudflared.TunnelToken);
    }

    public IReadOnlyList<ContainerSpec> BuildContainerSpecs(
        IConfiguration configuration,
        VolumePathResolver volumeResolver,
        ArrkInstance arrk)
    {
        CloudflaredConfig cloudflared = configuration
            .GetSection(CloudflaredConfig.SectionPath)
            .Get<CloudflaredConfig>() ?? new CloudflaredConfig();

        return
        [
            new()
            {
                Image = Image,
                Name = arrk.GetContainerName("cloudflared"),
                // The token is supplied through TUNNEL_TOKEN, so `tunnel run` picks it up.
                // Auto-update is disabled because Arrk owns image lifecycle management.
                Command = ["tunnel", "--no-autoupdate", "run"],
                Environment = new()
                {
                    ["TUNNEL_TOKEN"] = cloudflared.TunnelToken,
                    ["TZ"] = "Etc/UTC"
                },
                Labels = new()
                {
                    [ContainerLabels.Service] = StaticKey
                },
                // Outbound-only: share Arrk's network so the tunnel can resolve Traefik by
                // its container name. No ports are published to the host.
                NetworkName = arrk.NetworkName,
                RestartPolicy = RestartPolicyType.UnlessStopped
            }
        ];
    }
}
