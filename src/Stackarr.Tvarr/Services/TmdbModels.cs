using System.Text.Json.Serialization;

namespace Stackarr.Tvarr.Services;

/// <summary>
/// Shared TMDB DTOs. Kept close to the TMDB JSON shape so the client
/// stays thin and predictable. UI-facing mapping lives in <see cref="MediaCatalogService"/>.
/// </summary>

public static class TmdbMediaType
{
    public const string Movie = "movie";
    public const string Tv = "tv";
    public const string Person = "person";
    public const string All = "all";
}

public static class TmdbWindow
{
    public const string Day = "day";
    public const string Week = "week";
}

public class TmdbPagedResult<T>
{
    [JsonPropertyName("page")]
    public int Page { get; set; }

    [JsonPropertyName("results")]
    public List<T> Results { get; set; } = [];

    [JsonPropertyName("total_pages")]
    public int TotalPages { get; set; }

    [JsonPropertyName("total_results")]
    public int TotalResults { get; set; }
}

public class TmdbSearchResult
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("media_type")]
    public string? MediaType { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("original_title")]
    public string? OriginalTitle { get; set; }

    [JsonPropertyName("original_name")]
    public string? OriginalName { get; set; }

    [JsonPropertyName("overview")]
    public string Overview { get; set; } = "";

    [JsonPropertyName("poster_path")]
    public string? PosterPath { get; set; }

    [JsonPropertyName("backdrop_path")]
    public string? BackdropPath { get; set; }

    [JsonPropertyName("profile_path")]
    public string? ProfilePath { get; set; }

    [JsonPropertyName("release_date")]
    public string? ReleaseDate { get; set; }

    [JsonPropertyName("first_air_date")]
    public string? FirstAirDate { get; set; }

    [JsonPropertyName("vote_average")]
    public double VoteAverage { get; set; }

    [JsonPropertyName("vote_count")]
    public int VoteCount { get; set; }

    [JsonPropertyName("popularity")]
    public double Popularity { get; set; }

    [JsonPropertyName("genre_ids")]
    public List<int> GenreIds { get; set; } = [];

    [JsonPropertyName("original_language")]
    public string OriginalLanguage { get; set; } = "";

    [JsonPropertyName("origin_country")]
    public List<string> OriginCountry { get; set; } = [];

    [JsonPropertyName("adult")]
    public bool Adult { get; set; }

    [JsonPropertyName("known_for_department")]
    public string? KnownForDepartment { get; set; }

    [JsonIgnore]
    public string DisplayTitle => !string.IsNullOrWhiteSpace(Title) ? Title! : Name ?? "";

    [JsonIgnore]
    public string DisplayOriginalTitle => !string.IsNullOrWhiteSpace(OriginalTitle) ? OriginalTitle! : OriginalName ?? "";

    [JsonIgnore]
    public string? Date => !string.IsNullOrWhiteSpace(ReleaseDate) ? ReleaseDate : FirstAirDate;

    [JsonIgnore]
    public int Year => int.TryParse(Date?.Split('-').FirstOrDefault(), out int y) ? y : 0;

    [JsonIgnore]
    public bool IsTv => string.Equals(MediaType, TmdbMediaType.Tv, StringComparison.OrdinalIgnoreCase)
        || (string.IsNullOrEmpty(MediaType) && !string.IsNullOrWhiteSpace(FirstAirDate));
}

public class TmdbGenre
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";
}

public class TmdbGenreList
{
    [JsonPropertyName("genres")]
    public List<TmdbGenre> Genres { get; set; } = [];
}

public class TmdbExternalIds
{
    [JsonPropertyName("imdb_id")]
    public string? ImdbId { get; set; }

    [JsonPropertyName("tvdb_id")]
    public int? TvdbId { get; set; }

    [JsonPropertyName("facebook_id")]
    public string? FacebookId { get; set; }

    [JsonPropertyName("instagram_id")]
    public string? InstagramId { get; set; }

    [JsonPropertyName("twitter_id")]
    public string? TwitterId { get; set; }
}

public class TmdbCreditPerson
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("character")]
    public string? Character { get; set; }

    [JsonPropertyName("job")]
    public string? Job { get; set; }

    [JsonPropertyName("profile_path")]
    public string? ProfilePath { get; set; }

    [JsonPropertyName("order")]
    public int Order { get; set; }
}

public class TmdbCredits
{
    [JsonPropertyName("cast")]
    public List<TmdbCreditPerson> Cast { get; set; } = [];

    [JsonPropertyName("crew")]
    public List<TmdbCreditPerson> Crew { get; set; } = [];
}

/// <summary>
/// Person combined-credits response. Every row is shaped like a search result, which lets a person
/// query reuse the same mapping and filtering as discovery.
/// </summary>
public class TmdbPersonCredits
{
    [JsonPropertyName("cast")]
    public List<TmdbSearchResult> Cast { get; set; } = [];

    [JsonPropertyName("crew")]
    public List<TmdbSearchResult> Crew { get; set; } = [];
}

public class TmdbVideo
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("site")]
    public string Site { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("official")]
    public bool Official { get; set; }
}

public class TmdbVideos
{
    [JsonPropertyName("results")]
    public List<TmdbVideo> Results { get; set; } = [];
}

public class TmdbImage
{
    [JsonPropertyName("file_path")]
    public string? FilePath { get; set; }

    [JsonPropertyName("iso_639_1")]
    public string? Language { get; set; }

    [JsonPropertyName("vote_average")]
    public double VoteAverage { get; set; }
}

public class TmdbImages
{
    [JsonPropertyName("posters")]
    public List<TmdbImage> Posters { get; set; } = [];

    [JsonPropertyName("backdrops")]
    public List<TmdbImage> Backdrops { get; set; } = [];

    [JsonPropertyName("logos")]
    public List<TmdbImage> Logos { get; set; } = [];
}

public class TmdbReleaseDateEntry
{
    [JsonPropertyName("certification")]
    public string Certification { get; set; } = "";

    [JsonPropertyName("type")]
    public int Type { get; set; }

    [JsonPropertyName("release_date")]
    public string? ReleaseDate { get; set; }
}

/// <summary>TMDB release date types.</summary>
public static class TmdbReleaseType
{
    public const int Premiere = 1;
    public const int TheatricalLimited = 2;
    public const int Theatrical = 3;
    public const int Digital = 4;
    public const int Physical = 5;
    public const int Tv = 6;
}

public class TmdbReleaseDateCountry
{
    [JsonPropertyName("iso_3166_1")]
    public string Country { get; set; } = "";

    [JsonPropertyName("release_dates")]
    public List<TmdbReleaseDateEntry> ReleaseDates { get; set; } = [];
}

public class TmdbReleaseDates
{
    [JsonPropertyName("results")]
    public List<TmdbReleaseDateCountry> Results { get; set; } = [];
}

public class TmdbContentRating
{
    [JsonPropertyName("iso_3166_1")]
    public string Country { get; set; } = "";

    [JsonPropertyName("rating")]
    public string Rating { get; set; } = "";
}

public class TmdbContentRatings
{
    [JsonPropertyName("results")]
    public List<TmdbContentRating> Results { get; set; } = [];
}

public class TmdbNetwork
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("logo_path")]
    public string? LogoPath { get; set; }

    [JsonPropertyName("origin_country")]
    public string OriginCountry { get; set; } = "";
}

public class TmdbCollection
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("poster_path")]
    public string? PosterPath { get; set; }

    [JsonPropertyName("backdrop_path")]
    public string? BackdropPath { get; set; }
}

public class TmdbMovieDetail
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("original_title")]
    public string OriginalTitle { get; set; } = "";

    [JsonPropertyName("overview")]
    public string Overview { get; set; } = "";

    [JsonPropertyName("tagline")]
    public string Tagline { get; set; } = "";

    [JsonPropertyName("poster_path")]
    public string? PosterPath { get; set; }

    [JsonPropertyName("backdrop_path")]
    public string? BackdropPath { get; set; }

    [JsonPropertyName("release_date")]
    public string? ReleaseDate { get; set; }

    [JsonPropertyName("runtime")]
    public int? Runtime { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("vote_average")]
    public double VoteAverage { get; set; }

    [JsonPropertyName("vote_count")]
    public int VoteCount { get; set; }

    [JsonPropertyName("popularity")]
    public double Popularity { get; set; }

    [JsonPropertyName("original_language")]
    public string OriginalLanguage { get; set; } = "";

    [JsonPropertyName("adult")]
    public bool Adult { get; set; }

    [JsonPropertyName("genres")]
    public List<TmdbGenre> Genres { get; set; } = [];

    [JsonPropertyName("production_companies")]
    public List<TmdbNetwork> ProductionCompanies { get; set; } = [];

    [JsonPropertyName("external_ids")]
    public TmdbExternalIds? ExternalIds { get; set; }

    [JsonPropertyName("credits")]
    public TmdbCredits? Credits { get; set; }

    [JsonPropertyName("videos")]
    public TmdbVideos? Videos { get; set; }

    [JsonPropertyName("images")]
    public TmdbImages? Images { get; set; }

    [JsonPropertyName("release_dates")]
    public TmdbReleaseDates? ReleaseDates { get; set; }

    [JsonPropertyName("belongs_to_collection")]
    public TmdbCollection? BelongsToCollection { get; set; }

    [JsonIgnore]
    public int Year => int.TryParse(ReleaseDate?.Split('-').FirstOrDefault(), out int y) ? y : 0;
}

public class TmdbSeasonSummary
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("season_number")]
    public int SeasonNumber { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("overview")]
    public string Overview { get; set; } = "";

    [JsonPropertyName("poster_path")]
    public string? PosterPath { get; set; }

    [JsonPropertyName("air_date")]
    public string? AirDate { get; set; }

    [JsonPropertyName("episode_count")]
    public int EpisodeCount { get; set; }
}

public class TmdbTvDetail
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("original_name")]
    public string OriginalName { get; set; } = "";

    [JsonPropertyName("overview")]
    public string Overview { get; set; } = "";

    [JsonPropertyName("tagline")]
    public string Tagline { get; set; } = "";

    [JsonPropertyName("poster_path")]
    public string? PosterPath { get; set; }

    [JsonPropertyName("backdrop_path")]
    public string? BackdropPath { get; set; }

    [JsonPropertyName("first_air_date")]
    public string? FirstAirDate { get; set; }

    [JsonPropertyName("last_air_date")]
    public string? LastAirDate { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("number_of_seasons")]
    public int NumberOfSeasons { get; set; }

    [JsonPropertyName("number_of_episodes")]
    public int NumberOfEpisodes { get; set; }

    [JsonPropertyName("vote_average")]
    public double VoteAverage { get; set; }

    [JsonPropertyName("vote_count")]
    public int VoteCount { get; set; }

    [JsonPropertyName("popularity")]
    public double Popularity { get; set; }

    [JsonPropertyName("original_language")]
    public string OriginalLanguage { get; set; } = "";

    [JsonPropertyName("origin_country")]
    public List<string> OriginCountry { get; set; } = [];

    [JsonPropertyName("episode_run_time")]
    public List<int> EpisodeRunTime { get; set; } = [];

    [JsonPropertyName("genres")]
    public List<TmdbGenre> Genres { get; set; } = [];

    [JsonPropertyName("networks")]
    public List<TmdbNetwork> Networks { get; set; } = [];

    [JsonPropertyName("seasons")]
    public List<TmdbSeasonSummary> Seasons { get; set; } = [];

    [JsonPropertyName("external_ids")]
    public TmdbExternalIds? ExternalIds { get; set; }

    [JsonPropertyName("credits")]
    public TmdbCredits? Credits { get; set; }

    [JsonPropertyName("videos")]
    public TmdbVideos? Videos { get; set; }

    [JsonPropertyName("images")]
    public TmdbImages? Images { get; set; }

    [JsonPropertyName("content_ratings")]
    public TmdbContentRatings? ContentRatings { get; set; }

    [JsonIgnore]
    public int Year => int.TryParse(FirstAirDate?.Split('-').FirstOrDefault(), out int y) ? y : 0;

    [JsonIgnore]
    public int? Runtime => EpisodeRunTime.Count > 0 ? EpisodeRunTime[0] : null;
}

/// <summary>Options for a /discover query. Null/empty values are omitted from the request.</summary>
public class TmdbDiscoverQuery
{
    public string MediaType { get; set; } = TmdbMediaType.Movie;
    public int Page { get; set; } = 1;
    public string SortBy { get; set; } = "popularity.desc";
    public string? WithGenres { get; set; }
    public string? WithoutGenres { get; set; }
    public string? WithOriginalLanguage { get; set; }
    public string? WithOriginCountry { get; set; }
    public int? Year { get; set; }
    public string? ReleaseDateGte { get; set; }
    public string? ReleaseDateLte { get; set; }
    public string? FirstAirDateGte { get; set; }
    public string? FirstAirDateLte { get; set; }
    public int? VoteCountGte { get; set; }
    public double? VoteAverageGte { get; set; }
    public bool IncludeAdult { get; set; }
    public string? WithKeywords { get; set; }
    public string? WatchRegion { get; set; }
}
