using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace Stackarr.Jellyfin;

public class JellyfinConfig
{
    [Description("Enable Jellyfin media server")]
    public bool Enabled { get; set; } = true;

    [Description("Expose Jellyfin through the reverse proxy at /jellyfin")]
    public bool Proxy { get; set; } = true;

    [Description("Enable NVIDIA GPU hardware transcoding")]
    public bool Nvidia { get; set; }

    [Description("Server name shown to Jellyfin clients")]
    public string ServerName { get; set; } = "Stackarr";

    [Description("Administrator username created during first-time setup")]
    public string AdminUsername { get; set; } = "admin";

    [Description("Administrator password created during first-time setup")]
    [DataType(DataType.Password)]
    public string AdminPassword { get; set; } = "admin";

    [Description("Preferred metadata language code (e.g. en)")]
    public string MetadataLanguage { get; set; } = "en";

    [Description("Metadata country code (e.g. US, GB)")]
    public string MetadataCountry { get; set; } = "US";

    [Description("Apply the built-in Stackarr theme to the Jellyfin web client")]
    public bool Theme { get; set; } = true;

    [Description("Identify library items whose folders are named by TMDb/TVDB id so metadata and artwork resolve")]
    public bool ReconcileMetadata { get; set; } = true;

    [Description("Create and maintain Stackarr accounts in Jellyfin so one set of credentials works everywhere")]
    public bool ManageUsers { get; set; } = true;

    [Description("Install and configure the Jellyfin OIDC plugin so users can sign in with Stackarr")]
    public bool OidcSso { get; set; } = true;

    [Description("Jellyfin OIDC plugin repository (manifest) URL")]
    public string OidcPluginManifestUrl { get; set; } =
        "https://raw.githubusercontent.com/Ezeqielle/jellyfin-plugin-oidc/main/manifest.json";

    [Description("Pinned Jellyfin OIDC plugin version (empty = newest from the manifest)")]
    public string OidcPluginVersion { get; set; } = "";

    [Description("Stackarr role that maps to Jellyfin administrator")]
    public string OidcAdminRole { get; set; } = "Admin";

    [Description("Override the built-in Stackarr theme CSS (used when theme is true)")]
    public string CustomCss { get; set; } = "";

    [Description("Libraries to create and keep linked in Jellyfin")]
    public List<JellyfinLibraryConfig> Libraries { get; set; } = JellyfinLibraryConfig.Defaults();

    [Description("In-site web player that plays media through Jellyfin's own engine")]
    public JellyfinWebPlayerConfig WebPlayer { get; set; } = new();

    [Description("Record what each user watches (including external Jellyfin clients) for history and staleness")]
    public bool ActivityTracking { get; set; } = true;

    [Description("How often to poll Jellyfin sessions for device-level watch activity (seconds)")]
    public int ActivityPollSeconds { get; set; } = 20;
}

/// <summary>
/// The in-site web player. It is a thin, removable layer in front of Jellyfin: Stackarr asks
/// Jellyfin for a playable stream and proxies it, so browsers can play x265/MKV content using
/// Jellyfin's remux/transcode. Turn it off (or delete the module types) and the UI falls back to
/// Jellyfin's own web client.
/// </summary>
public class JellyfinWebPlayerConfig
{
    [Description("Enable the in-site web player (Play buttons open the player instead of Jellyfin)")]
    public bool Enabled { get; set; } = true;
}

/// <summary>Describes a Jellyfin library Stackarr keeps linked to a media folder.</summary>
public class JellyfinLibraryConfig
{
    [Description("Library display name")]
    public string Name { get; set; } = "";

    [Description("Jellyfin collection type: movies, tvshows, music or homevideos")]
    [AllowedValues("movies", "tvshows", "music", "homevideos")]
    public string CollectionType { get; set; } = "movies";

    [Description("Path inside the Jellyfin container (e.g. /movies, /shows, /music)")]
    public string Path { get; set; } = "";

    [Description("Whether this library is managed")]
    public bool Enabled { get; set; } = true;

    public static List<JellyfinLibraryConfig> Defaults() =>
    [
        new() { Name = "Movies", CollectionType = "movies", Path = "/movies" },
        new() { Name = "TV Shows", CollectionType = "tvshows", Path = "/shows" },
        new() { Name = "Music", CollectionType = "music", Path = "/music" }
    ];
}
