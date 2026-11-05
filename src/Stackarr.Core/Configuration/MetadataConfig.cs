using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace Stackarr.Core.Configuration;

/// <summary>
/// External metadata provider credentials (e.g. TMDB, Last.fm) used for artwork,
/// descriptions and search. Kept separate from application settings so the
/// credentials live in their own clearly-labelled section.
/// </summary>
public class MetadataConfig
{
    [Description("TMDB v3 API key. Preferred over Radarr/Sonarr lookups when set (alongside the read token).")]
    [Display(Name = "TMDB API key")]
    [DataType(DataType.Password)]
    public string TmdbApiKey { get; set; } = "";

    [Description("TMDB v4 read access token. Preferred auth method for metadata and search.")]
    [Display(Name = "TMDB read token")]
    [DataType(DataType.Password)]
    public string TmdbReadToken { get; set; } = "";
}
