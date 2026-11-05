using System.Text.Json.Serialization;

namespace Stackarr.Bazarr;

/// <summary>Bazarr wraps most list responses in a <c>data</c> envelope.</summary>
public sealed class BazarrEnvelope<T>
{
    [JsonPropertyName("data")]
    public T Data { get; set; } = default!;

    [JsonPropertyName("total")]
    public int Total { get; set; }
}

public sealed class BazarrSystemStatus
{
    [JsonPropertyName("bazarr_version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("sonarr_version")]
    public string SonarrVersion { get; set; } = "";

    [JsonPropertyName("radarr_version")]
    public string RadarrVersion { get; set; } = "";
}

public class BazarrMediaBase
{
    public string Title { get; set; } = "";

    public int Year { get; set; }

    public string Path { get; set; } = "";

    public bool Monitored { get; set; }

    public int? ProfileId { get; set; }

    [JsonPropertyName("missing_subtitles")]
    public List<BazarrLanguage> MissingSubtitles { get; set; } = [];

    public List<BazarrSubtitle> Subtitles { get; set; } = [];
}

public sealed class BazarrMovie : BazarrMediaBase
{
    [JsonPropertyName("radarrId")]
    public int RadarrId { get; set; }

    [JsonPropertyName("imdbId")]
    public string ImdbId { get; set; } = "";
}

public sealed class BazarrSeries : BazarrMediaBase
{
    [JsonPropertyName("sonarrSeriesId")]
    public int SonarrSeriesId { get; set; }

    [JsonPropertyName("tvdbId")]
    public int TvdbId { get; set; }

    [JsonPropertyName("episodeFileCount")]
    public int EpisodeFileCount { get; set; }

    [JsonPropertyName("episodeMissingCount")]
    public int EpisodeMissingCount { get; set; }
}

public sealed class BazarrEpisode : BazarrMediaBase
{
    [JsonPropertyName("sonarrEpisodeId")]
    public int SonarrEpisodeId { get; set; }

    [JsonPropertyName("sonarrSeriesId")]
    public int SonarrSeriesId { get; set; }

    public int Season { get; set; }

    public int Episode { get; set; }
}

public sealed class BazarrSubtitle
{
    public string Name { get; set; } = "";

    public string Code2 { get; set; } = "";

    public string Code3 { get; set; } = "";

    public string Path { get; set; } = "";

    public bool Forced { get; set; }

    [JsonPropertyName("hi")]
    public bool HearingImpaired { get; set; }
}

public sealed class BazarrLanguage
{
    public string Name { get; set; } = "";

    public string Code2 { get; set; } = "";

    public string Code3 { get; set; } = "";

    public bool Forced { get; set; }

    [JsonPropertyName("hi")]
    public bool HearingImpaired { get; set; }
}

public sealed class BazarrProviderCandidate
{
    public string Provider { get; set; } = "";

    /// <summary>Provider-specific subtitle id (Bazarr calls this <c>subtitle</c>).</summary>
    [JsonPropertyName("subtitle")]
    public string Subtitle { get; set; } = "";

    /// <summary>Bazarr returns the language display name here, not a language object.</summary>
    public string Language { get; set; } = "";

    public string Uploader { get; set; } = "";

    public int Score { get; set; }

    [JsonPropertyName("orig_score")]
    public int OriginalScore { get; set; }

    [JsonPropertyName("score_without_hash")]
    public int ScoreWithoutHash { get; set; }

    [JsonPropertyName("release_info")]
    public List<string> ReleaseInfo { get; set; } = [];

    public string Url { get; set; } = "";

    [JsonPropertyName("hearing_impaired")]
    public string HearingImpaired { get; set; } = "";

    public string Forced { get; set; } = "";

    [JsonPropertyName("original_format")]
    public string OriginalFormat { get; set; } = "";

    [JsonPropertyName("matches")]
    public List<string> Matches { get; set; } = [];
}

public sealed class BazarrLanguageProfile
{
    public int ProfileId { get; set; }

    public string Name { get; set; } = "";

    public int? Cutoff { get; set; }

    public List<BazarrLanguageProfileItem> Items { get; set; } = [];

    public List<string> MustContain { get; set; } = [];

    public List<string> MustNotContain { get; set; } = [];

    public bool? OriginalFormat { get; set; }

    public string? Tag { get; set; }
}

public sealed class BazarrLanguageProfileItem
{
    public int Id { get; set; }

    public string Language { get; set; } = "";

    [JsonPropertyName("audio_exclude")]
    public string AudioExclude { get; set; } = "False";

    [JsonPropertyName("audio_only_include")]
    public string AudioOnlyInclude { get; set; } = "False";

    public string Hi { get; set; } = "False";

    public string Forced { get; set; } = "False";
}

public sealed class BazarrSettings
{
    public BazarrGeneralSettings General { get; set; } = new();

    public BazarrServiceSettings Sonarr { get; set; } = new();

    public BazarrServiceSettings Radarr { get; set; } = new();
}

public sealed class BazarrGeneralSettings
{
    public bool UseSonarr { get; set; }

    public bool UseRadarr { get; set; }

    public List<string> EnabledProviders { get; set; } = [];

    [JsonPropertyName("serie_default_enabled")]
    public bool SeriesDefaultEnabled { get; set; }

    [JsonPropertyName("serie_default_profile")]
    public int? SeriesDefaultProfile { get; set; }

    public bool MovieDefaultEnabled { get; set; }

    public int? MovieDefaultProfile { get; set; }
}

public sealed class BazarrServiceSettings
{
    public string Ip { get; set; } = "";

    public int Port { get; set; }

    public string BaseUrl { get; set; } = "/";

    public string ApiKey { get; set; } = "";

    public bool Ssl { get; set; }
}
