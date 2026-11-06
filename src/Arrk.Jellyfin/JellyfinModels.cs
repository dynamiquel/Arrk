using System.Text.Json;
using System.Text.Json.Serialization;

namespace Arrk.Jellyfin;

/// <summary>Public (unauthenticated) server information returned by <c>/System/Info/Public</c>.</summary>
public class JellyfinPublicSystemInfo
{
    public string ServerName { get; set; } = "";
    public string Version { get; set; } = "";
    public string Id { get; set; } = "";
    public string ProductName { get; set; } = "";
    public bool StartupWizardCompleted { get; set; }
}

/// <summary>Payload for the first-run startup wizard (<c>/Startup/Configuration</c>).</summary>
public class JellyfinStartupConfiguration
{
    public string ServerName { get; set; } = "";
    public string UICulture { get; set; } = "en-US";
    public string MetadataCountryCode { get; set; } = "US";
    public string PreferredMetadataLanguage { get; set; } = "en";
}

/// <summary>Payload for <c>/Startup/User</c>, the first (administrator) account.</summary>
public class JellyfinStartupUser
{
    public string Name { get; set; } = "";
    public string Password { get; set; } = "";
}

/// <summary>Result of <c>/Users/AuthenticateByName</c>.</summary>
public class JellyfinAuthenticationResult
{
    public string AccessToken { get; set; } = "";
    public string ServerId { get; set; } = "";
    public JellyfinUser? User { get; set; }
}

public class JellyfinUser
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool HasPassword { get; set; }
    public JellyfinUserPolicy? Policy { get; set; }

    /// <summary>
    /// Unknown properties round-trip untouched so renaming a user never discards fields
    /// Arrk does not model.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement> Extra { get; set; } = [];
}

public class JellyfinUserPolicy
{
    public bool IsAdministrator { get; set; }
    public bool IsHidden { get; set; }
    public bool IsDisabled { get; set; }
    public bool EnableAllFolders { get; set; } = true;

    /// <summary>Preserves policy fields Arrk does not explicitly manage.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement> Extra { get; set; } = [];
}

/// <summary>
/// Jellyfin's server configuration. Only the fields Arrk manages are modelled; every
/// other property round-trips untouched through <see cref="Extra"/> so an update never wipes
/// settings owned by the user or another tool.
/// </summary>
public class JellyfinServerConfiguration
{
    public string ServerName { get; set; } = "";
    public string PreferredMetadataLanguage { get; set; } = "en";
    public string MetadataCountryCode { get; set; } = "US";
    public bool IsStartupWizardCompleted { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> Extra { get; set; } = [];
}

public class JellyfinBrandingOptions
{
    public string LoginDisclaimer { get; set; } = "";
    public string CustomCss { get; set; } = "";
    public bool SplashscreenEnabled { get; set; }
}

public class JellyfinVirtualFolder
{
    public string Name { get; set; } = "";
    public List<string> Locations { get; set; } = [];
    public string? CollectionType { get; set; }
    public string ItemId { get; set; } = "";
    public JellyfinLibraryOptions LibraryOptions { get; set; } = new();
}

/// <summary>
/// Library options. As with <see cref="JellyfinServerConfiguration"/>, unknown properties are
/// preserved via <see cref="Extra"/> so patching metadata providers does not discard the rest
/// of the server's defaults.
/// </summary>
public class JellyfinLibraryOptions
{
    public bool Enabled { get; set; } = true;
    public bool EnableInternetProviders { get; set; } = true;
    public bool EnableRealtimeMonitor { get; set; } = true;
    public string PreferredMetadataLanguage { get; set; } = "en";
    public string MetadataCountryCode { get; set; } = "US";
    public List<JellyfinMediaPathInfo> PathInfos { get; set; } = [];
    public List<JellyfinTypeOptions> TypeOptions { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement> Extra { get; set; } = [];
}

public class JellyfinMediaPathInfo
{
    public string Path { get; set; } = "";

    [JsonExtensionData]
    public Dictionary<string, JsonElement> Extra { get; set; } = [];
}

public class JellyfinTypeOptions
{
    public string Type { get; set; } = "";
    public List<string> MetadataFetchers { get; set; } = [];
    public List<string> MetadataFetcherOrder { get; set; } = [];
    public List<string> ImageFetchers { get; set; } = [];
    public List<string> ImageFetcherOrder { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement> Extra { get; set; } = [];
}

public class JellyfinItemsResult
{
    public List<JellyfinItem> Items { get; set; } = [];
    public int TotalRecordCount { get; set; }
}

public class JellyfinItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public string? Path { get; set; }
    public int? ParentIndexNumber { get; set; }
    public int? IndexNumber { get; set; }
    public int? ChildCount { get; set; }
    public int? RecursiveItemCount { get; set; }
    public int? ProductionYear { get; set; }
    public string? Overview { get; set; }
    public long? RunTimeTicks { get; set; }
    public string? SeriesId { get; set; }
    public string? SeriesName { get; set; }
    public Dictionary<string, string> ProviderIds { get; set; } = [];
    public JellyfinUserItemData? UserData { get; set; }

    /// <summary>
    /// The item's playable versions. The single-item endpoint returns these by default; the player
    /// needs the id so Jellyfin honours an explicit audio/subtitle stream selection.
    /// </summary>
    public List<Playback.JellyfinMediaSource> MediaSources { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement> Extra { get; set; } = [];
}

/// <summary>Per-user playback state attached to a Jellyfin item.</summary>
public class JellyfinUserItemData
{
    public bool Played { get; set; }
    public double PlayedPercentage { get; set; }
    public long PlaybackPositionTicks { get; set; }
    public DateTime? LastPlayedDate { get; set; }
    public int PlayCount { get; set; }
    public int UnplayedItemCount { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> Extra { get; set; } = [];
}

/// <summary>Query for the Jellyfin <c>/Items</c> endpoint. Only the parameters Arrk uses.</summary>
public class JellyfinItemQuery
{
    public string? ParentId { get; set; }
    public string? IncludeItemTypes { get; set; }
    public string? MediaTypes { get; set; }
    public string? SearchTerm { get; set; }
    public bool Recursive { get; set; }
    public int? Limit { get; set; }
    public int? StartIndex { get; set; }
    public string? Filters { get; set; }
    public string? SortBy { get; set; }
    public string? SortOrder { get; set; }
    public bool EnableUserData { get; set; }
    public List<string> Fields { get; set; } = [];

    /// <summary>Direct provider lookup, e.g. <c>tmdb.1273221</c> or <c>tvdb.121361</c> (what Jellyseerr uses).</summary>
    public string? AnyProviderIdEquals { get; set; }

    /// <summary>When false, only returns items that do not yet have a TMDb id (used for reconciliation).</summary>
    public bool? HasTmdbId { get; set; }

    /// <summary>When false, only returns items that do not yet have a TVDB id (used for reconciliation).</summary>
    public bool? HasTvdbId { get; set; }
}

/// <summary>An installed Jellyfin plugin as returned by <c>/Plugins</c>.</summary>
public class JellyfinPluginInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Status { get; set; } = "";

    [JsonExtensionData]
    public Dictionary<string, JsonElement> Extra { get; set; } = [];
}

/// <summary>A Jellyfin plugin repository entry (<c>/Repositories</c>).</summary>
public class JellyfinRepositoryInfo
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

/// <summary>A package offered by a configured repository (<c>/Packages</c>).</summary>
public class JellyfinPackageInfo
{
    public string Name { get; set; } = "";
    public string Guid { get; set; } = "";
    public List<JellyfinPackageVersion> Versions { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement> Extra { get; set; } = [];
}

public class JellyfinPackageVersion
{
    public string Version { get; set; } = "";
    public string? TargetAbi { get; set; }
    public string? SourceUrl { get; set; }
    public string? Checksum { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> Extra { get; set; } = [];
}

/// <summary>A single candidate returned by Jellyfin's remote metadata search.</summary>
public class JellyfinRemoteSearchResult
{
    public string Name { get; set; } = "";
    public Dictionary<string, string> ProviderIds { get; set; } = [];
    public int? ProductionYear { get; set; }
    public string? ImageUrl { get; set; }
    public string? Overview { get; set; }
    public string? SearchProviderName { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> Extra { get; set; } = [];
}

/// <summary>An active Jellyfin session. Carries the device/client identity for watch history.</summary>
public class JellyfinSession
{
    public string Id { get; set; } = "";
    public string? UserId { get; set; }
    public string? UserName { get; set; }
    public string? DeviceName { get; set; }
    public string? Client { get; set; }
    public string? DeviceId { get; set; }
    public string? RemoteEndPoint { get; set; }
    public string? PlaySessionId { get; set; }
    public DateTime? LastActivityDate { get; set; }
    public JellyfinPlayState? PlayState { get; set; }
    public JellyfinItem? NowPlayingItem { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> Extra { get; set; } = [];
}

public class JellyfinPlayState
{
    public long PositionTicks { get; set; }
    public bool IsPaused { get; set; }
    public string? PlayMethod { get; set; }
    public string? MediaSourceId { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> Extra { get; set; } = [];
}

