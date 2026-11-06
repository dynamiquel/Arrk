using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Arrk.Core;
using Arrk.Core.Configuration;
using Arrk.Core.Modules;
using Arrk.Core.Services;
using Arrk.Docker;
using Arrk.Navidrome.Configurations;
using Arrk.Navidrome.Playback;
using Arrk.Ui;

namespace Arrk.Navidrome;

public class NavidromeModule : IArrkModule
{
    public string ModuleName => "Navidrome";
    public string ModuleDescription => "Navidrome music player and playback proxy.";

    public IReadOnlyList<Service> GetServices()
    {
        return [new(new NavidromeServiceDefinition())];
    }

    public IReadOnlyList<IConfigSectionProvider> GetConfigSections() =>
        [new ConfigSectionProvider<NavidromeConfig>("services:navidrome", "Navidrome",
            icon: "MusicNote", group: "Services", order: 70, groupOrder: 20)];

    public IReadOnlyList<ModuleDependency> GetDependencies() =>
    [
        new("Navidrome", "GPL-3.0", "deluan", "https://github.com/navidrome/navidrome", "Modern music server and streamer compatible with Subsonic/Airsonic"),
    ];

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<NavidromeConfig>(configuration.GetSection("services:navidrome"));

        services.AddSingleton(sp =>
        {
            ArrkInstance arrk = sp.GetRequiredService<ArrkInstance>();
            IOptionsMonitor<NavidromeConfig> config = sp.GetRequiredService<IOptionsMonitor<NavidromeConfig>>();
            NavidromeApiSecret secret = sp.GetRequiredService<NavidromeApiSecret>();
            // Resolve the sub-path per call so toggling services.navidrome.proxy (which also changes
            // the container's ND_BASEURL) is picked up without a restart.
            return new NavidromeClient(
                () => $"http://{arrk.GetContainerName("navidrome")}:4533{(config.CurrentValue.Proxy ? "/navidrome/" : "/")}",
                secret.Username,
                secret.Password);
        });
        services.AddSingleton<NavidromeService>();
        services.AddSingleton<IModuleEntityConfiguration, MusicPlaylistOwnerConfiguration>();
        services.AddSingleton<IModuleEntityConfiguration, MusicPlayHistoryConfiguration>();
        services.AddSingleton<IModuleEntityConfiguration, MusicSubscriptionConfiguration>();
        services.AddSingleton<MusicLibraryService>();
    }

    public void ConfigureHostedServices(IServiceCollection services)
    {
        services.AddHostedService<NavidromeAccountProvisioner>();
    }

    /// <summary>The module owns its streaming/cover proxy endpoints.</summary>
    public void ConfigureEndpoints(IEndpointRouteBuilder endpoints) => endpoints.MapNavidromePlaybackEndpoints();
}
