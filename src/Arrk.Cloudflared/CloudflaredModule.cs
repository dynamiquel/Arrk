using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Arrk.Core.Configuration;
using Arrk.Core.Modules;
using Arrk.Core.Services;

namespace Arrk.Cloudflared;

public class CloudflaredModule : IArrkModule
{
    public string ModuleName => "Cloudflared";
    public string ModuleDescription => "Cloudflare tunnel for remote access without host ports.";

    public IReadOnlyList<Service> GetServices()
    {
        return [new(new CloudflaredServiceDefinition())];
    }

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<CloudflaredConfig>(configuration.GetSection(CloudflaredConfig.SectionPath));
    }

    public IReadOnlyList<ModuleDependency> GetDependencies() =>
    [
        new("cloudflared", "Apache-2.0", "Cloudflare", "https://github.com/cloudflare/cloudflared", "Cloudflare Tunnel client"),
    ];
}
