using MudBlazor;
using Stackarr.MediaLifecycle.Components.Tiles;
using Stackarr.Ui;

namespace Stackarr.MediaLifecycle;

/// <summary>The lifecycle summary tiles. They are admin-only because the cleanup page is.</summary>
public sealed class MediaLifecycleTileProvider : ITileProvider
{
    public IReadOnlyList<TileDefinition> Tiles { get; } =
    [
        Create("lifecycle.stale", "Stale now", Icons.Material.Filled.WarningAmber, "stale", 10, null),
        Create("lifecycle.expiring", "About to be stale", Icons.Material.Filled.HourglassBottom, "expiring", 20, null),
        Create("lifecycle.reclaimable", "Reclaimable", Icons.Material.Filled.Storage, "reclaimable", 30, null),
        Create("lifecycle.autocleanup", "Auto cleanup", Icons.Material.Filled.AutoMode, "autocleanup", 40, null)
    ];

    static TileDefinition Create(string id, string title, string icon, string metric, int order, string? defaultSection) =>
        new(
            id,
            "Lifecycle",
            title,
            icon,
            typeof(LifecycleStatTile),
            Order: order,
            Description: $"{title} from the media lifecycle report.",
            Href: "/stale",
            AdminOnly: true,
            DefaultSection: defaultSection,
            Parameters: new Dictionary<string, object?> { ["Metric"] = metric });
}
