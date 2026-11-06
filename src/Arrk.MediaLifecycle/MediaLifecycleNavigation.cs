using MudBlazor;
using Arrk.Ui;

namespace Arrk.MediaLifecycle;

public sealed class MediaLifecycleNavigation : INavigationProvider
{
    public IReadOnlyList<NavigationEntry> Entries { get; } =
    [
        new(UiSections.System, "Stale Media", "/stale", Icons.Material.Filled.CleaningServices,
            Order: 55, AdminOnly: true)
    ];
}

public sealed class MediaLifecycleHeadAssets : IHeadAssetProvider
{
    public IReadOnlyList<HeadAsset> Assets { get; } =
    [
        new("_content/Arrk.MediaLifecycle/css/lifecycle.css", HeadAssetKind.Style, Order: 132)
    ];
}
