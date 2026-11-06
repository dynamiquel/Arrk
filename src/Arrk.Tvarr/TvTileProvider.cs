using MudBlazor;
using Arrk.Tvarr.Components.Tiles;
using Arrk.Ui;

namespace Arrk.Tvarr;

/// <summary>
/// Movie and show library summaries, contributed as tiles. They read the same Radarr/Sonarr data
/// as the library pages through a shared short-lived snapshot.
/// </summary>
public sealed class TvTileProvider : ITileProvider
{
    public IReadOnlyList<TileDefinition> Tiles { get; } =
    [
        new(
            "movies.library", "Movies", "In Library", Icons.Material.Filled.Movie, typeof(MovieStatTile),
            Description: "Movies currently in the Radarr library.", Href: "/movies",
            EnabledConfigPath: "services:tv",
            Parameters: new Dictionary<string, object?> { ["Metric"] = "library" }),
        new(
            "movies.downloaded", "Movies", "Downloaded", Icons.Material.Filled.CheckCircle, typeof(MovieStatTile),
            Description: "Movies with a file on disk.", Href: "/movies",
            EnabledConfigPath: "services:tv",
            Parameters: new Dictionary<string, object?> { ["Metric"] = "downloaded" }),
        new(
            "movies.missing", "Movies", "Missing", Icons.Material.Filled.ErrorOutline, typeof(MovieStatTile),
            Description: "Movies without a file.", Href: "/movies",
            EnabledConfigPath: "services:tv",
            Parameters: new Dictionary<string, object?> { ["Metric"] = "missing" }),
        new(
            "movies.downloading", "Movies", "Downloading", Icons.Material.Filled.CloudDownload, typeof(MovieStatTile),
            Description: "Movies active in the Radarr queue.", Href: "/movies",
            EnabledConfigPath: "services:tv",
            Parameters: new Dictionary<string, object?> { ["Metric"] = "downloading" }),
        new(
            "movies.ondisk", "Movies", "On Disk", Icons.Material.Filled.Storage, typeof(MovieStatTile),
            Description: "Disk space used by the movie library.", Href: "/movies",
            EnabledConfigPath: "services:tv",
            Parameters: new Dictionary<string, object?> { ["Metric"] = "ondisk" }),
        new(
            "movies.monitored", "Movies", "Monitored", Icons.Material.Filled.Visibility, typeof(MovieStatTile),
            Description: "Movies being watched for releases.", Href: "/movies",
            EnabledConfigPath: "services:tv",
            Parameters: new Dictionary<string, object?> { ["Metric"] = "monitored" }),

        new(
            "shows.shows", "TV", "Shows", Icons.Material.Filled.Tv, typeof(ShowStatTile),
            Description: "Series currently in the Sonarr library.", Href: "/tv",
            EnabledConfigPath: "services:tv",
            Parameters: new Dictionary<string, object?> { ["Metric"] = "shows" }),
        new(
            "shows.seasons", "TV", "Seasons", Icons.Material.Filled.Collections, typeof(ShowStatTile),
            Description: "Seasons tracked across all shows.", Href: "/tv",
            EnabledConfigPath: "services:tv",
            Parameters: new Dictionary<string, object?> { ["Metric"] = "seasons" }),
        new(
            "shows.episodes", "TV", "Episodes", Icons.Material.Filled.CheckCircle, typeof(ShowStatTile),
            Description: "Downloaded episodes against all aired episodes.", Href: "/tv",
            EnabledConfigPath: "services:tv",
            Parameters: new Dictionary<string, object?> { ["Metric"] = "episodes" }),
        new(
            "shows.missing", "TV", "Missing", Icons.Material.Filled.ErrorOutline, typeof(ShowStatTile),
            Description: "Episodes still to grab.", Href: "/tv",
            EnabledConfigPath: "services:tv",
            Parameters: new Dictionary<string, object?> { ["Metric"] = "missing" }),
        new(
            "shows.downloading", "TV", "Downloading", Icons.Material.Filled.CloudDownload, typeof(ShowStatTile),
            Description: "Episodes active in the Sonarr queue.", Href: "/tv",
            EnabledConfigPath: "services:tv",
            Parameters: new Dictionary<string, object?> { ["Metric"] = "downloading" }),
        new(
            "shows.ondisk", "TV", "On Disk", Icons.Material.Filled.Storage, typeof(ShowStatTile),
            Description: "Disk space used by the show library.", Href: "/tv",
            EnabledConfigPath: "services:tv",
            Parameters: new Dictionary<string, object?> { ["Metric"] = "ondisk" }),

        // Interactive shelves from the Movies/Shows pages. They are not part of the default
        // dashboard but can be added from the editor; the dashboard version is unified.
        new(
            "tv.continue-watching", "TV", "Continue Watching", Icons.Material.Filled.PlayCircle,
            typeof(ContinueWatchingTile), Description: "Resume movies and episodes in progress, across the whole library.",
            EnabledConfigPath: "services:tv", DefaultSize: TileSize.VeryLarge),
        new(
            "tv.watchlists", "TV", "Watchlists", Icons.Material.Filled.Bookmark,
            typeof(WatchlistTile), Description: "Your watchlists with movie and TV titles together.",
            EnabledConfigPath: "services:tv", DefaultSize: TileSize.VeryLarge)
    ];
}
