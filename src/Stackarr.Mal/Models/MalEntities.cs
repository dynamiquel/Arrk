namespace Stackarr.Mal.Models;

/// <summary>One Stackarr user's linked MyAnimeList account. Tokens are stored encrypted.</summary>
public class MalAccount
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public int MalUserId { get; set; }
    public string MalUsername { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public DateTime AccessExpiresAt { get; set; }
    public DateTime LinkedAt { get; set; }
    public DateTime? LastPulledAt { get; set; }
    public DateTime? LastSyncedAt { get; set; }
    public bool SyncEnabled { get; set; } = true;
    public bool ProtectProgress { get; set; } = true;
    public bool NeedsReauth { get; set; }
    public string? LastError { get; set; }
    public string? LastSyncSummary { get; set; }
}

/// <summary>A cached entry from the user's MAL anime list.</summary>
public class MalListEntry
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public int MalId { get; set; }
    public string Status { get; set; } = "plan_to_watch";
    public int Score { get; set; }
    public int WatchedEpisodes { get; set; }
    public bool IsRewatching { get; set; }
    public string Tags { get; set; } = "";
    public string Comments { get; set; } = "";
    public DateTime? MalUpdatedAt { get; set; }
    public DateTime SyncedAt { get; set; }
}

/// <summary>Cached MAL metadata, enriched with TMDB metadata where a mapping exists.</summary>
public class MalCatalogEntry
{
    public int MalId { get; set; }
    public string Title { get; set; } = "";
    public string EnglishTitle { get; set; } = "";
    public string JapaneseTitle { get; set; } = "";
    public string Synonyms { get; set; } = "";
    public string RelatedAnimeJson { get; set; } = "";
    public string MainPictureUrl { get; set; } = "";
    public string MediaType { get; set; } = "";
    public int NumEpisodes { get; set; }
    public string AiringStatus { get; set; } = "";
    public string StartDate { get; set; } = "";
    public string EndDate { get; set; } = "";
    public string Synopsis { get; set; } = "";
    public string Genres { get; set; } = "";
    public string Studios { get; set; } = "";
    public double? Mean { get; set; }
    public int? Rank { get; set; }
    public bool Nsfw { get; set; }

    public int? TmdbId { get; set; }
    public string TmdbKind { get; set; } = "";
    public int? TmdbSeason { get; set; }
    public string PosterUrl { get; set; } = "";
    public string DisplayTitle { get; set; } = "";
    public string DisplayOverview { get; set; } = "";
    public int? Year { get; set; }

    public DateTime FetchedAt { get; set; }
    public DateTime? EnrichedAt { get; set; }
}

/// <summary>One local season (or movie) mapped to one or more MAL entries.</summary>
public class MalMap
{
    public int Id { get; set; }
    public string Provider { get; set; } = "tmdb";
    public int ProviderId { get; set; }
    public string Kind { get; set; } = "series";
    public int Season { get; set; }
    public string State { get; set; } = "auto";
    public int LocalEpisodeCount { get; set; }
    public DateTime UpdatedAt { get; set; }
    public List<MalMapPart> Parts { get; set; } = [];
}

/// <summary>One MAL entry within a local season, with the local/MAL episode start pair.</summary>
public class MalMapPart
{
    public int Id { get; set; }
    public int MalMapId { get; set; }
    public MalMap? Map { get; set; }
    public int MalId { get; set; }
    public int LocalEpisodeStart { get; set; }
    public int LocalEpisodeEnd { get; set; }
    public int MalEpisodeStart { get; set; }
    public string Source { get; set; } = "dataset";
}
