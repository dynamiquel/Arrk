using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Stackarr.Core.Configuration;
using Stackarr.Core.Modules;

namespace Stackarr.Core.Services;

public class GluetunModule : IStackarrModule
{
    public string ModuleName => "VPN";
    public string ModuleDescription => "VPN tunnel that routes downloads through WireGuard.";

    /// <summary>
    /// Static API key for Gluetun's control server. Mirrors the internal-service key
    /// convention (<c>ProwlarrModule.ServiceApiKey</c>, <c>TvModule.ServiceApiKey</c>); the
    /// control server is only reachable on the internal Docker network.
    /// </summary>
    public const string ControlApiKey = "stackarr";

    public IReadOnlyList<Service> GetServices()
    {
        return [new(new GluetunServiceDefinition())];
    }

    public IReadOnlyList<ModuleDependency> GetDependencies() =>
    [
        new("Gluetun", "GPL-3.0", "qdm12", "https://github.com/qdm12/gluetun", "VPN client in a thin Docker container with multiple VPN provider support"),
    ];

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<VpnConfig>(configuration.GetSection("vpn"));
        services.AddSingleton<GluetunControlClient>();
    }
}
