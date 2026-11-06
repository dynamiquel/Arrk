using MudBlazor;
using Stackarr.MediaLifecycle.Components.Tiles;
using Stackarr.Ui;

namespace Stackarr.MediaLifecycle;

/// <summary>The lifecycle summary tiles. They are admin-only because the cleanup page is.</summary>
public sealed class MediaLifecycleTileProvider : ITileProvider
{
    public IReadOnlyList<TileDefinition> Tiles { get; } =
    [
        new(
            "lifecycle.stale", "Lifecycle", "Stale now", Icons.Material.Filled.WarningAmber,
            typeof(LifecycleStatTile), Description: "Stale now from the media lifecycle report.",
            Href: "/stale", AdminOnly: true,
            Parameters: new Dictionary<string, object?> { ["Metric"] = "stale" }),
        new(
            "lifecycle.expiring", "Lifecycle", "About to be stale", Icons.Material.Filled.HourglassBottom,
            typeof(LifecycleStatTile), Description: "About to be stale from the media lifecycle report.",
            Href: "/stale", AdminOnly: true,
            Parameters: new Dictionary<string, object?> { ["Metric"] = "expiring" }),
        new(
            "lifecycle.reclaimable", "Lifecycle", "Reclaimable", Icons.Material.Filled.Storage,
            typeof(LifecycleStatTile), Description: "Reclaimable from the media lifecycle report.",
            Href: "/stale", AdminOnly: true,
            Parameters: new Dictionary<string, object?> { ["Metric"] = "reclaimable" }),
        new(
            "lifecycle.autocleanup", "Lifecycle", "Auto cleanup", Icons.Material.Filled.AutoMode,
            typeof(LifecycleStatTile), Description: "Auto cleanup from the media lifecycle report.",
            Href: "/stale", AdminOnly: true,
            Parameters: new Dictionary<string, object?> { ["Metric"] = "autocleanup" })
    ];
}
