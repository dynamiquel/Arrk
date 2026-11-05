using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Stackarr.Core.Configuration;
using Stackarr.Core.Modules;
using Stackarr.Core.Services;

namespace Stackarr.Cloudflared;

public class CloudflaredModule : IStackarrModule
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
