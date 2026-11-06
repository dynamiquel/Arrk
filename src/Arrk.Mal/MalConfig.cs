using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace Arrk.Mal;

/// <summary>MyAnimeList OAuth and sync settings.</summary>
public class MalConfig
{
    public const string SectionPath = "services:mal";
    public const string DefaultDatasetUrl =
        "https://raw.githubusercontent.com/Fribb/anime-lists/master/anime-list-mini.json";

    [Description("Enable the MyAnimeList integration")]
    public bool Enabled { get; set; } = true;

    [Description("MyAnimeList API client id from https://myanimelist.net/apiconfig")]
    public string ClientId { get; set; } = "";

    [Description("MyAnimeList API client secret. Register the redirect URI shown on the MyAnimeList page.")]
    [DataType(DataType.Password)]
    public string ClientSecret { get; set; } = "";

    [Description("Allowed OAuth redirect URIs. The first matching the current origin is used; defaults to routes.publicUrl + /mal-signin")]
    public List<string> RedirectUris { get; set; } = [];

    [Description("Allow signing in to Arrk with MyAnimeList (in addition to linking it from My account)")]
    public bool AllowLogin { get; set; } = true;

    [Description("How often to push completed watch progress to MyAnimeList, in minutes")]
    [Range(1, 1440)]
    public int SyncMinutes { get; set; } = 10;

    [Description("MyAnimeList API base URL")]
    public string ApiBaseUrl { get; set; } = "https://api.myanimelist.net/v2";

    [Description("MyAnimeList OAuth base URL")]
    public string OAuthBaseUrl { get; set; } = "https://myanimelist.net/v1/oauth2";

    [Description("Mapping dataset URL used to translate local seasons to MAL entries")]
    public string DatasetUrl { get; set; } = DefaultDatasetUrl;

    [Description("Days before the anime ID mapping dataset is refreshed")]
    [Range(1, 90)]
    public int DatasetRefreshDays { get; set; } = 7;

    [Description("Skip mapping entries whose MAL media type is not a TV series or movie")]
    public bool SkipSpecialEntries { get; set; } = true;

    public bool IsConfigured =>
        Enabled
        && !string.IsNullOrWhiteSpace(ClientId)
        && !string.IsNullOrWhiteSpace(ClientSecret);
}
