using MudBlazor;
using Stackarr.Tvarr.Components.Tiles;
using Stackarr.Ui;

namespace Stackarr.Tvarr;

/// <summary>
/// Movie and show library summaries, contributed as tiles. They read the same Radarr/Sonarr data
/// as the library pages through a shared short-lived snapshot.
/// </summary>
public sealed class TvTileProvider : ITileProvider
{
    public IReadOnlyList<TileDefinition> Tiles { get; } =
    [
        Create("movies.library", "Movies", "In Library", Icons.Material.Filled.Movie, "library", 10,
            "/movies", "Movies currently in the Radarr library.", null),
        Create("movies.downloaded", "Movies", "Downloaded", Icons.Material.Filled.CheckCircle, "downloaded", 20,
            "/movies", "Movies with a file on disk.", null),
        Create("movies.missing", "Movies", "Missing", Icons.Material.Filled.ErrorOutline, "missing", 30,
            "/movies", "Movies without a file.", null),
        Create("movies.downloading", "Movies", "Downloading", Icons.Material.Filled.CloudDownload, "downloading", 40,
            "/movies", "Movies active in the Radarr queue.", null),
        Create("movies.ondisk", "Movies", "On Disk", Icons.Material.Filled.Storage, "ondisk", 50,
            "/movies", "Disk space used by the movie library.", null),
        Create("movies.monitored", "Movies", "Monitored", Icons.Material.Filled.Visibility, "monitored", 60,
            "/movies", "Movies being watched for releases.", null),

        Create("shows.shows", "TV", "Shows", Icons.Material.Filled.Tv, "shows", 10,
            "/tv", "Series currently in the Sonarr library.", null, typeof(ShowStatTile)),
        Create("shows.seasons", "TV", "Seasons", Icons.Material.Filled.Collections, "seasons", 20,
            "/tv", "Seasons tracked across all shows.", null, typeof(ShowStatTile)),
        Create("shows.episodes", "TV", "Episodes", Icons.Material.Filled.CheckCircle, "episodes", 30,
            "/tv", "Downloaded episodes against all aired episodes.", null, typeof(ShowStatTile)),
        Create("shows.missing", "TV", "Missing", Icons.Material.Filled.ErrorOutline, "missing", 40,
            "/tv", "Episodes still to grab.", null, typeof(ShowStatTile)),
        Create("shows.downloading", "TV", "Downloading", Icons.Material.Filled.CloudDownload, "downloading", 50,
            "/tv", "Episodes active in the Sonarr queue.", null, typeof(ShowStatTile)),
        Create("shows.ondisk", "TV", "On Disk", Icons.Material.Filled.Storage, "ondisk", 60,
            "/tv", "Disk space used by the show library.", null, typeof(ShowStatTile))
    ];

    static TileDefinition Create(
        string id,
        string category,
        string title,
        string icon,
        string metric,
        int order,
        string href,
        string description,
        string? defaultSection,
        Type? component = null) =>
        new(
            id,
            category,
            title,
            icon,
            component ?? typeof(MovieStatTile),
            Order: order,
            Description: description,
            Href: href,
            EnabledConfigPath: "services:tv",
            DefaultSection: defaultSection,
            Parameters: new Dictionary<string, object?> { ["Metric"] = metric });
}
