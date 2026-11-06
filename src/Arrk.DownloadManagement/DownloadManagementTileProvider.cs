using MudBlazor;
using Arrk.DownloadManagement.Components.Shared;
using Arrk.DownloadManagement.Components.Tiles;
using Arrk.Ui;

namespace Arrk.DownloadManagement;

/// <summary>
/// Contributes the torrent transfer summaries and the storage tile. The same definitions are used
/// by the Downloads page (without customisation) and by the dashboard, where the shared frame adds
/// the <c>TORRENTS - …</c> context.
/// </summary>
public sealed class DownloadManagementTileProvider : ITileProvider
{
    public IReadOnlyList<TileDefinition> Tiles { get; } =
    [
        new(
            "torrents.download",
            "Torrents",
            "Download",
            Icons.Material.Filled.ArrowDownward,
            typeof(TorrentStatTile),
            Description: "Current download speed and total downloaded.",
            Href: "/downloads",
            Parameters: new Dictionary<string, object?> { ["Metric"] = "download" }),
        new(
            "torrents.upload",
            "Torrents",
            "Upload",
            Icons.Material.Filled.ArrowUpward,
            typeof(TorrentStatTile),
            Description: "Current upload speed and total uploaded.",
            Href: "/downloads",
            Parameters: new Dictionary<string, object?> { ["Metric"] = "upload" }),
        new(
            "torrents.active",
            "Torrents",
            "Active",
            Icons.Material.Filled.Cloud,
            typeof(TorrentStatTile),
            Description: "Torrents currently downloading.",
            Href: "/downloads",
            Parameters: new Dictionary<string, object?> { ["Metric"] = "active" }),
        new(
            "torrents.seeding",
            "Torrents",
            "Seeding",
            Icons.Material.Filled.Speed,
            typeof(TorrentStatTile),
            Description: "Torrents currently seeding.",
            Href: "/downloads",
            Parameters: new Dictionary<string, object?> { ["Metric"] = "seeding" }),
        new(
            "system.storage",
            "System",
            "Storage",
            Icons.Material.Filled.Storage,
            typeof(StorageTile),
            Description: "Free space on the media and downloads roots.",
            Href: "/downloads")
    ];
}
