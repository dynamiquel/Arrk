using Arrk.Ui;

namespace Arrk.App.Services.Ui;

/// <summary>
/// The shell's own scripts, registered through the same head-asset seam a module uses. Assets that
/// belong to the shared component library (theme styles, the tab/scroll/swipe scripts) are supplied
/// by <see cref="UiRegistry"/> itself; module-specific scripts are registered by their module.
/// </summary>
public sealed class AppHeadAssetProvider : IHeadAssetProvider
{
    public IReadOnlyList<HeadAsset> Assets { get; } =
    [
        new("js/logViewer.js", HeadAssetKind.Script, Order: 110),
        new("js/arrk-chrome.js", HeadAssetKind.Script, Order: 140)
    ];
}
