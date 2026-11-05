using MudBlazor;
using Stackarr.Music.Components.Tiles;
using Stackarr.Ui;

namespace Stackarr.Music;

/// <summary>The native music library's headline counts, contributed as shareable tiles.</summary>
public sealed class MusicTileProvider : ITileProvider
{
    public IReadOnlyList<TileDefinition> Tiles { get; } =
    [
        Create("music.albums", "Albums", Icons.Material.Filled.Album, "albums", 10, null),
        Create("music.songs", "Songs", Icons.Material.Filled.MusicNote, "songs", 20, null),
        Create("music.downloaded", "Downloaded", Icons.Material.Filled.CheckCircle, "downloaded", 30, null),
        Create("music.missing", "Missing", Icons.Material.Filled.ErrorOutline, "missing", 40, null),
        Create("music.downloading", "Downloading", Icons.Material.Filled.CloudDownload, "downloading", 50, null),
        Create("music.ondisk", "On Disk", Icons.Material.Filled.Storage, "ondisk", 60, null)
    ];

    static TileDefinition Create(string id, string title, string icon, string metric, int order, string? defaultSection) =>
        new(
            id,
            "Music",
            title,
            icon,
            typeof(MusicStatTile),
            Order: order,
            Description: $"{title} in the native music library.",
            Href: "/music",
            EnabledConfigPath: "services:music",
            DefaultSection: defaultSection,
            Parameters: new Dictionary<string, object?> { ["Metric"] = metric });
}
