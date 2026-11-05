using MudBlazor;
using Stackarr.Ui;

namespace Stackarr.MediaLifecycle;

public sealed class MediaLifecycleNavigation : INavigationProvider
{
    public IReadOnlyList<NavigationEntry> Entries { get; } =
    [
        new(UiSections.System, "Stale media", "/stale", Icons.Material.Filled.CleaningServices,
            Order: 55, AdminOnly: true)
    ];
}

public sealed class MediaLifecycleHeadAssets : IHeadAssetProvider
{
    public IReadOnlyList<HeadAsset> Assets { get; } =
    [
        new("_content/Stackarr.MediaLifecycle/css/lifecycle.css", HeadAssetKind.Style, Order: 132)
    ];
}
