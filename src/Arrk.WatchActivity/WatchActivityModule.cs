using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Arrk.Core.Models;
using Arrk.Core.Modules;
using Arrk.Core.Services;
using Arrk.Ui;

namespace Arrk.WatchActivity;

/// <summary>
/// Presents the watch/listen history recorded in Core. The module owns no data and no jobs; it is
/// purely a UI contribution, so plugins can record activity without depending on this assembly.
/// </summary>
public class WatchActivityModule : IArrkModule
{
    public string ModuleName => "Watch activity";
    public string ModuleDescription => "Per-user watch and listen history across every player.";

    public IReadOnlyList<Service> GetServices() => [];

    public IReadOnlyList<ModuleDependency> GetDependencies() => [];

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<INavigationProvider, WatchActivityNavigation>();
        services.AddSingleton<IHeadAssetProvider, WatchActivityHeadAssets>();
        services.AddSingleton<ITileProvider, WatchActivityTileProvider>();
    }
}

public sealed class WatchActivityNavigation : INavigationProvider
{
    public IReadOnlyList<NavigationEntry> Entries { get; } =
    [
        new(UiSections.Browse, "History", "/history", MudBlazor.Icons.Material.Filled.History,
            Order: 24)
    ];
}

public sealed class WatchActivityHeadAssets : IHeadAssetProvider
{
    public IReadOnlyList<HeadAsset> Assets { get; } =
    [
        new("_content/Arrk.WatchActivity/css/history.css", HeadAssetKind.Style, Order: 133)
    ];
}
