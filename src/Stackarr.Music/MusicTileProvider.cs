using MudBlazor;
using Stackarr.Music.Components.Tiles;
using Stackarr.Ui;

namespace Stackarr.Music;

/// <summary>The native music library's headline counts and shelves, contributed as shareable tiles.</summary>
public sealed class MusicTileProvider : ITileProvider
{
    public IReadOnlyList<TileDefinition> Tiles { get; } =
    [
        new(
            "music.albums", "Music", "Albums", Icons.Material.Filled.Album, typeof(MusicStatTile),
            Description: "Albums in the native music library.", Href: "/music",
            EnabledConfigPath: "services:music",
            Parameters: new Dictionary<string, object?> { ["Metric"] = "albums" }),
        new(
            "music.songs", "Music", "Songs", Icons.Material.Filled.MusicNote, typeof(MusicStatTile),
            Description: "Songs in the native music library.", Href: "/music",
            EnabledConfigPath: "services:music",
            Parameters: new Dictionary<string, object?> { ["Metric"] = "songs" }),
        new(
            "music.downloaded", "Music", "Downloaded", Icons.Material.Filled.CheckCircle, typeof(MusicStatTile),
            Description: "Downloaded in the native music library.", Href: "/music",
            EnabledConfigPath: "services:music",
            Parameters: new Dictionary<string, object?> { ["Metric"] = "downloaded" }),
        new(
            "music.missing", "Music", "Missing", Icons.Material.Filled.ErrorOutline, typeof(MusicStatTile),
            Description: "Missing in the native music library.", Href: "/music",
            EnabledConfigPath: "services:music",
            Parameters: new Dictionary<string, object?> { ["Metric"] = "missing" }),
        new(
            "music.downloading", "Music", "Downloading", Icons.Material.Filled.CloudDownload, typeof(MusicStatTile),
            Description: "Downloading in the native music library.", Href: "/music",
            EnabledConfigPath: "services:music",
            Parameters: new Dictionary<string, object?> { ["Metric"] = "downloading" }),
        new(
            "music.ondisk", "Music", "On Disk", Icons.Material.Filled.Storage, typeof(MusicStatTile),
            Description: "On Disk in the native music library.", Href: "/music",
            EnabledConfigPath: "services:music",
            Parameters: new Dictionary<string, object?> { ["Metric"] = "ondisk" }),

        // Interactive shelves from the Music page. The dashboard versions hide the shelf heading
        // because the tile frame already supplies the title.
        new(
            "music.recently-played", "Music", "Recently Played", Icons.Material.Filled.Replay,
            typeof(RecentlyPlayedTile), Description: "Resume the current queue, recent playlists and recently played songs.",
            EnabledConfigPath: "services:music", DefaultSize: TileSize.Large),
        new(
            "music.playlists", "Music", "Playlists", Icons.Material.Filled.QueueMusic,
            typeof(MusicPlaylistsTile), Description: "Your music playlists, ready to open or play.",
            EnabledConfigPath: "services:music", DefaultSize: TileSize.Large)
    ];
}
