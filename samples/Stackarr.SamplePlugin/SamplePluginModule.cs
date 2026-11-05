using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using Stackarr.Core.Modules;
using Stackarr.Core.Services;
using Stackarr.Ui;

namespace Stackarr.SamplePlugin;

/// <summary>
/// The sample plugin's module. A drop-in DLL only needs an <see cref="IStackarrModule"/>; the host
/// discovers it from the plugins folder and wires its contributions exactly like a built-in module.
/// </summary>
public sealed class SamplePluginModule : IStackarrModule
{
    public string ModuleName => "SamplePlugin";

    public IReadOnlyList<Service> GetServices() => [];

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<INavigationProvider, SamplePluginNavigation>();
        services.AddSingleton<IHeadAssetProvider, SamplePluginHeadAssets>();
    }
}

/// <summary>Contributes the "Sample" entry to the Info section.</summary>
public sealed class SamplePluginNavigation : INavigationProvider
{
    public IReadOnlyList<NavigationEntry> Entries { get; } =
    [
        new(UiSections.Info, "Sample plugin", "/sample", Icons.Material.Filled.Extension, Order: 50)
    ];
}

/// <summary>
/// The plugin's own stylesheet. A runtime-loaded assembly has no <c>_content</c> manifest, so the
/// asset is served from the plugins folder at <c>/plugins/Stackarr.SamplePlugin/sample.css</c>.
/// </summary>
public sealed class SamplePluginHeadAssets : IHeadAssetProvider
{
    public IReadOnlyList<HeadAsset> Assets { get; } =
    [
        new("/plugins/Stackarr.SamplePlugin/sample.css", HeadAssetKind.Style, Order: 200)
    ];
}
