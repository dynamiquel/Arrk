using Arrk.Core.Modules;

namespace Arrk.Core.Services;

public class TraefikModule : IArrkModule
{
    public string ModuleName => "ReverseProxy";
    public string ModuleDescription => "Reverse proxy and routing for the managed services.";

    public IReadOnlyList<Service> GetServices()
    {
        return [new(new TraefikServiceDefinition())];
    }

    public IReadOnlyList<ModuleDependency> GetDependencies() =>
    [
        new("Traefik", "MIT", "Traefik Labs", "https://github.com/traefik/traefik", "Cloud native application proxy"),
    ];
}
