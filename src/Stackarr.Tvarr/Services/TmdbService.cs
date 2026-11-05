using Stackarr.Ui.Components;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Stackarr.Tvarr.Components.Shared;

namespace Stackarr.Tvarr.Services;

/// <summary>
/// Thin, resilient client for The Movie Database (TMDB) v3 API.
/// Supports both the v4 read access token (preferred) and a v3 API key.
/// This is the preferred metadata/search provider for Stackarr; callers fall
/// back to Radarr/Sonarr lookups when <see cref="IsConfigured"/> is false.
/// </summary>
public class TmdbService
{
    const string BaseUrl = "https://api.themoviedb.org/3";
    const string ImageBaseUrl = "https://image.tmdb.org/t/p";
    const string DefaultLanguage = "en-GB";
    const string DefaultRegion = "GB";

    static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);
    const int MaxCacheEntries = 1500;

    readonly Func<(string ApiKey, string ReadToken)> _credentials;
    readonly ILogger<TmdbService>? _logger;
    readonly HttpClient _http;
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, CacheEntry> _cache = new();

    string _lastApiKey = "\0";
    string _lastReadToken = "\0";

    sealed record CacheEntry(DateTime ExpiresAt, string Json);

    public TmdbService(string apiKey, string readToken = "", ILogger<TmdbService>? logger = null)
        : this(apiKey, readToken, logger, null)
    {
    }

    /// <summary>Test-friendly constructor allowing a custom transport (e.g. a fake handler).</summary>
    public TmdbService(string apiKey, string readToken, ILogger<TmdbService>? logger, HttpMessageHandler? handler)
        : this(() => (apiKey ?? "", readToken ?? ""), logger, handler)
    {
    }

    /// <summary>
    /// Live-credentials constructor: the supplied delegate is invoked on every request so changing
    /// <c>metadata.tmdb_api_key</c> / <c>metadata.tmdb_read_token</c> in the config editor applies
    /// immediately (and the response cache is dropped when the credentials change).
    /// </summary>
    public TmdbService(Func<(string ApiKey, string ReadToken)> credentials, ILogger<TmdbService>? logger, HttpMessageHandler? handler = null)
    {
        _credentials = credentials;
        _logger = logger;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        // Trailing slash is required so relative paths keep the "/3" version segment.
        _http.BaseAddress = new Uri(BaseUrl + "/");
        _http.Timeout = TimeSpan.FromSeconds(20);
    }

    (string ApiKey, string ReadToken) Credentials
    {
        get
        {
            (string apiKey, string readToken) = _credentials();
            apiKey ??= "";
            readToken ??= "";
            if (!string.Equals(apiKey, _lastApiKey, StringComparison.Ordinal)
                || !string.Equals(readToken, _lastReadToken, StringComparison.Ordinal))
            {
                _lastApiKey = apiKey;
                _lastReadToken = readToken;
                _cache.Clear();
            }

            return (apiKey, readToken);
        }
    }

    /// <summary>True when either a read token or API key has been supplied.</summary>
    public bool IsConfigured
    {
        get
        {
            (string apiKey, string readToken) = Credentials;
            return !string.IsNullOrWhiteSpace(readToken) || !string.IsNullOrWhiteSpace(apiKey);
        }
    }

    //  Search

    public Task<List<TmdbSearchResult>> SearchMultiAsync(string query, int page = 1, bool includeAdult = false, CancellationToken ct = default)
        => SearchAsync(TmdbMediaType.All, query, page, null, includeAdult, ct);

    public Task<List<TmdbSearchResult>> SearchMoviesAsync(string query, int page = 1, int? year = null, CancellationToken ct = default)
        => SearchAsync(TmdbMediaType.Movie, query, page, year, false, ct);

    public Task<List<TmdbSearchResult>> SearchTvAsync(string query, int page = 1, int? year = null, CancellationToken ct = default)
        => SearchAsync(TmdbMediaType.Tv, query, page, year, false, ct);

    public async Task<List<TmdbSearchResult>> SearchAsync(
        string mediaType, string query, int page = 1, int? year = null, bool includeAdult = false, CancellationToken ct = default)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(query))
            return [];

        string type = mediaType switch
        {
            TmdbMediaType.Movie => "movie",
            TmdbMediaType.Tv => "tv",
            _ => "multi"
        };

        Dictionary<string, string?> queryParams = new()
        {
            ["query"] = query,
            ["page"] = Math.Clamp(page, 1, 500).ToString(),
            ["include_adult"] = includeAdult ? "true" : "false"
        };

        if (year.HasValue)
        {
            queryParams[type == "movie" ? "year" : "first_air_date_year"] = year.Value.ToString();
        }

        TmdbPagedResult<TmdbSearchResult>? result =
            await GetAsync<TmdbPagedResult<TmdbSearchResult>>($"/search/{type}", queryParams, ct);

        return result?.Results ?? [];
    }

    /// <summary>Searches people (actors, directors) by name, most relevant first.</summary>
    public async Task<List<TmdbSearchResult>> SearchPeopleAsync(
        string query, int page = 1, CancellationToken ct = default)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(query))
            return [];

        TmdbPagedResult<TmdbSearchResult>? result =
            await GetAsync<TmdbPagedResult<TmdbSearchResult>>("/search/person", new()
            {
                ["query"] = query,
                ["page"] = Math.Clamp(page, 1, 500).ToString(),
                ["include_adult"] = "false"
            }, ct);

        return result?.Results ?? [];
    }

    /// <summary>
    /// Every film and series a person is credited on. Used to answer "what's that new movie with X"
    /// and to intersect a cast member with the user's library.
    /// </summary>
    public async Task<TmdbPersonCredits?> GetPersonCreditsAsync(
        int personId, CancellationToken ct = default)
    {
        if (!IsConfigured || personId <= 0)
            return null;

        return await GetAsync<TmdbPersonCredits>($"/person/{personId}/combined_credits", null, ct);
    }

    //  Trending / curated lists

    public async Task<List<TmdbSearchResult>> GetTrendingAsync(string mediaType = TmdbMediaType.All, string window = TmdbWindow.Week, int page = 1, CancellationToken ct = default)
    {
        if (!IsConfigured) return [];
        string type = mediaType switch
        {
            TmdbMediaType.Movie => "movie",
            TmdbMediaType.Tv => "tv",
            _ => "all"
        };
        TmdbPagedResult<TmdbSearchResult>? result =
            await GetAsync<TmdbPagedResult<TmdbSearchResult>>($"/trending/{type}/{window}", new() { ["page"] = page.ToString() }, ct);
        return result?.Results ?? [];
    }

    /// <summary>Popular / top_rated / upcoming / now_playing (movie) or popular / top_rated / on_the_air / airing_today (tv).</summary>
    public async Task<List<TmdbSearchResult>> GetListAsync(string mediaType, string list, int page = 1, CancellationToken ct = default)
    {
        if (!IsConfigured) return [];
        string type = string.Equals(mediaType, TmdbMediaType.Tv, StringComparison.OrdinalIgnoreCase) ? "tv" : "movie";
        TmdbPagedResult<TmdbSearchResult>? result =
            await GetAsync<TmdbPagedResult<TmdbSearchResult>>($"/{type}/{list}", new() { ["page"] = page.ToString() }, ct);
        return result?.Results ?? [];
    }

    public async Task<List<TmdbSearchResult>> GetMovieRecommendationsAsync(int tmdbId, int page = 1, CancellationToken ct = default)
    {
        if (!IsConfigured) return [];
        TmdbPagedResult<TmdbSearchResult>? result =
            await GetAsync<TmdbPagedResult<TmdbSearchResult>>($"/movie/{tmdbId}/recommendations", new() { ["page"] = page.ToString() }, ct);
        return result?.Results ?? [];
    }

    public async Task<List<TmdbSearchResult>> GetTvRecommendationsAsync(int tmdbId, int page = 1, CancellationToken ct = default)
    {
        if (!IsConfigured) return [];
        TmdbPagedResult<TmdbSearchResult>? result =
            await GetAsync<TmdbPagedResult<TmdbSearchResult>>($"/tv/{tmdbId}/recommendations", new() { ["page"] = page.ToString() }, ct);
        return result?.Results ?? [];
    }

    /// <summary>Finds a release date of the given type, preferring the supplied countries in order.</summary>
    public static string? FindReleaseDate(TmdbReleaseDates? releaseDates, int type, params string[] preferredCountries)
    {
        if (releaseDates?.Results == null || releaseDates.Results.Count == 0)
            return null;

        IEnumerable<TmdbReleaseDateCountry> ordered = releaseDates.Results;
        if (preferredCountries.Length > 0)
        {
            ordered = releaseDates.Results.OrderBy(c =>
            {
                int index = Array.FindIndex(preferredCountries, p => string.Equals(p, c.Country, StringComparison.OrdinalIgnoreCase));
                return index < 0 ? preferredCountries.Length : index;
            });
        }

        foreach (TmdbReleaseDateCountry country in ordered)
        {
            TmdbReleaseDateEntry? entry = country.ReleaseDates
                .FirstOrDefault(r => r.Type == type && !string.IsNullOrWhiteSpace(r.ReleaseDate));
            if (entry?.ReleaseDate != null)
                return entry.ReleaseDate;
        }
        return null;
    }

    //  Discover (genre / country / date filtered)

    public async Task<List<TmdbSearchResult>> DiscoverAsync(TmdbDiscoverQuery query, CancellationToken ct = default)
    {
        if (!IsConfigured) return [];

        string type = string.Equals(query.MediaType, TmdbMediaType.Tv, StringComparison.OrdinalIgnoreCase) ? "tv" : "movie";

        Dictionary<string, string?> p = new()
        {
            ["page"] = Math.Clamp(query.Page, 1, 500).ToString(),
            ["sort_by"] = string.IsNullOrWhiteSpace(query.SortBy) ? "popularity.desc" : query.SortBy,
            ["include_adult"] = query.IncludeAdult ? "true" : "false"
        };

        void Add(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) p[key] = value;
        }

        Add("with_genres", query.WithGenres);
        Add("without_genres", query.WithoutGenres);
        Add("with_original_language", query.WithOriginalLanguage);
        Add("with_origin_country", query.WithOriginCountry);
        Add("with_keywords", query.WithKeywords);
        Add("watch_region", query.WatchRegion);
        if (query.VoteCountGte.HasValue) p["vote_count.gte"] = query.VoteCountGte.Value.ToString();
        if (query.VoteAverageGte.HasValue) p["vote_average.gte"] = query.VoteAverageGte.Value.ToString("0.#");

        if (type == "movie")
        {
            if (query.Year.HasValue) p["primary_release_year"] = query.Year.Value.ToString();
            Add("primary_release_date.gte", query.ReleaseDateGte);
            Add("primary_release_date.lte", query.ReleaseDateLte);
        }
        else
        {
            if (query.Year.HasValue) p["first_air_date_year"] = query.Year.Value.ToString();
            Add("first_air_date.gte", query.FirstAirDateGte);
            Add("first_air_date.lte", query.FirstAirDateLte);
        }

        TmdbPagedResult<TmdbSearchResult>? result =
            await GetAsync<TmdbPagedResult<TmdbSearchResult>>($"/discover/{type}", p, ct);
        return result?.Results ?? [];
    }

    //  Details

    public Task<TmdbMovieDetail?> GetMovieDetailAsync(int tmdbId, CancellationToken ct = default)
        => GetAsync<TmdbMovieDetail>($"/movie/{tmdbId}",
            new()
            {
                ["append_to_response"] = "external_ids,credits,videos,images,release_dates",
                // Without this, the default language filter hides logos/artwork.
                ["include_image_language"] = "en,null"
            }, ct);

    public Task<TmdbTvDetail?> GetTvDetailAsync(int tmdbId, CancellationToken ct = default)
        => GetAsync<TmdbTvDetail>($"/tv/{tmdbId}",
            new()
            {
                ["append_to_response"] = "external_ids,credits,videos,images,content_ratings",
                ["include_image_language"] = "en,null"
            }, ct);

    /// <summary>
    /// Lightweight check for whether a movie has no web or physical release yet, used to badge
    /// catalog cards. Fetches only the release dates and resolves them through
    /// <see cref="ReleaseStatus"/>, so the card badge and the media-popup badge agree.
    /// </summary>
    public async Task<bool> IsMovieNotYetReleasedAsync(int tmdbId, CancellationToken ct = default)
    {
        if (!IsConfigured || tmdbId <= 0)
        {
            return false;
        }

        TmdbMovieDetail? detail = await GetAsync<TmdbMovieDetail>($"/movie/{tmdbId}",
            new() { ["append_to_response"] = "release_dates" }, ct);
        if (detail == null)
        {
            return false;
        }

        DateTime? inCinemas = FormatHelper.ParseReleaseDate(FindReleaseDate(detail.ReleaseDates, TmdbReleaseType.Theatrical, "GB", "US"))
            ?? FormatHelper.ParseReleaseDate(detail.ReleaseDate);
        DateTime? digital = FormatHelper.ParseReleaseDate(FindReleaseDate(detail.ReleaseDates, TmdbReleaseType.Digital, "GB", "US"));
        DateTime? physical = FormatHelper.ParseReleaseDate(FindReleaseDate(detail.ReleaseDates, TmdbReleaseType.Physical, "GB", "US"));

        return ReleaseStatus.MovieNotYetReleased(detail.Status, inCinemas, digital, physical);
    }

    /// <summary>Lightweight logo lookup (only the images payload), used for card hover artwork.</summary>
    public async Task<List<TmdbImage>> GetLogosAsync(string mediaType, int tmdbId, CancellationToken ct = default)
    {
        if (!IsConfigured || tmdbId <= 0) return [];
        string type = string.Equals(mediaType, TmdbMediaType.Tv, StringComparison.OrdinalIgnoreCase) ? "tv" : "movie";
        TmdbImages? images = await GetAsync<TmdbImages>($"/{type}/{tmdbId}/images",
            new() { ["include_image_language"] = "en,null" }, ct);
        return images?.Logos ?? [];
    }

    public async Task<List<TmdbGenre>> GetGenresAsync(string mediaType, CancellationToken ct = default)
    {
        if (!IsConfigured) return [];
        string type = string.Equals(mediaType, TmdbMediaType.Tv, StringComparison.OrdinalIgnoreCase) ? "tv" : "movie";
        TmdbGenreList? result = await GetAsync<TmdbGenreList>($"/genre/{type}/list", null, ct);
        return result?.Genres ?? [];
    }

    //  Season and episode detail used by the series detail dialog

    public async Task<TmdbTvSeason?> GetTvSeasonAsync(int tmdbId, int seasonNumber, CancellationToken ct = default)
    {
        if (!IsConfigured) return null;
        return await GetAsync<TmdbTvSeason>($"/tv/{tmdbId}/season/{seasonNumber}",
            new() { ["append_to_response"] = "credits,images" }, ct);
    }

    public async Task<TmdbTvEpisode?> GetTvEpisodeAsync(int tmdbId, int seasonNumber, int episodeNumber, CancellationToken ct = default)
    {
        if (!IsConfigured) return null;
        return await GetAsync<TmdbTvEpisode>($"/tv/{tmdbId}/season/{seasonNumber}/episode/{episodeNumber}",
            new() { ["append_to_response"] = "credits,images" }, ct);
    }

    public async Task<TmdbTvShow?> GetTvShowAsync(int tmdbId, CancellationToken ct = default)
    {
        if (!IsConfigured) return null;
        TmdbTvDetail? detail = await GetTvDetailAsync(tmdbId, ct);
        if (detail == null) return null;
        return new TmdbTvShow
        {
            BackdropPath = detail.BackdropPath,
            PosterPath = detail.PosterPath,
            VoteAverage = detail.VoteAverage,
            VoteCount = detail.VoteCount,
            NumberOfSeasons = detail.NumberOfSeasons,
            NumberOfEpisodes = detail.NumberOfEpisodes,
            FirstAirDate = detail.FirstAirDate,
            Status = detail.Status,
            Overview = detail.Overview,
            Name = detail.Name,
            OriginalName = detail.OriginalName,
            Genres = detail.Genres.Select(g => g.Name).ToList(),
            OriginCountry = detail.OriginCountry,
            Networks = detail.Networks.Select(n => n.Name).ToList()
        };
    }

    public async Task<TmdbMovie?> GetMovieAsync(int tmdbId, CancellationToken ct = default)
    {
        if (!IsConfigured) return null;
        TmdbMovieDetail? detail = await GetMovieDetailAsync(tmdbId, ct);
        if (detail == null) return null;
        return new TmdbMovie
        {
            BackdropPath = detail.BackdropPath,
            PosterPath = detail.PosterPath,
            VoteAverage = detail.VoteAverage,
            VoteCount = detail.VoteCount,
            Runtime = detail.Runtime,
            ReleaseDate = detail.ReleaseDate,
            Status = detail.Status,
            Overview = detail.Overview,
            Title = detail.Title,
            OriginalTitle = detail.OriginalTitle,
            Genres = detail.Genres.Select(g => g.Name).ToList()
        };
    }

    //  Image helpers

    public static string GetPosterUrl(string? posterPath, string size = "w500")
        => string.IsNullOrEmpty(posterPath) ? "" : $"{ImageBaseUrl}/{size}{posterPath}";

    public static string GetBackdropUrl(string? backdropPath, string size = "w780")
        => string.IsNullOrEmpty(backdropPath) ? "" : $"{ImageBaseUrl}/{size}{backdropPath}";

    public static string GetStillUrl(string? stillPath, string size = "w300")
        => string.IsNullOrEmpty(stillPath) ? "" : $"{ImageBaseUrl}/{size}{stillPath}";

    public static string GetProfileUrl(string? profilePath, string size = "w185")
        => string.IsNullOrEmpty(profilePath) ? "" : $"{ImageBaseUrl}/{size}{profilePath}";

    public static string GetLogoUrl(string? logoPath, string size = "w92")
        => string.IsNullOrEmpty(logoPath) ? "" : $"{ImageBaseUrl}/{size}{logoPath}";

    /// <summary>Picks the best title logo (prefers English, then highest voted).</summary>
    public static string FindBestLogo(IEnumerable<TmdbImage>? logos)
    {
        if (logos == null) return "";
        List<TmdbImage> list = logos.Where(l => !string.IsNullOrEmpty(l.FilePath)).ToList();
        if (list.Count == 0) return "";

        TmdbImage? best = list.Where(l => l.Language == "en").OrderByDescending(l => l.VoteAverage).FirstOrDefault()
            ?? list.Where(l => string.IsNullOrEmpty(l.Language)).OrderByDescending(l => l.VoteAverage).FirstOrDefault()
            ?? list.OrderByDescending(l => l.VoteAverage).FirstOrDefault();
        return best?.FilePath is { } path ? GetLogoUrl(path, "w300") : "";
    }

    /// <summary>Picks the best YouTube trailer/teaser key from a TMDB videos list.</summary>
    public static string? FindYouTubeTrailer(IEnumerable<TmdbVideo>? videos)
    {
        if (videos == null) return null;
        List<TmdbVideo> list = videos.Where(v => v.Site == "YouTube").ToList();
        if (list.Count == 0) return null;

        TmdbVideo? trailer = list.FirstOrDefault(v => v.Type == "Trailer" && v.Official)
            ?? list.FirstOrDefault(v => v.Type == "Trailer")
            ?? list.FirstOrDefault(v => v.Type == "Teaser")
            ?? list.FirstOrDefault();
        return trailer?.Key;
    }

    //  Transport

    async Task<T?> GetAsync<T>(string path, Dictionary<string, string?>? queryParams, CancellationToken ct) where T : class
    {
        if (!IsConfigured) return null;

        try
        {
            List<string> parts = [];
            parts.Add($"language={Uri.EscapeDataString(DefaultLanguage)}");
            parts.Add($"region={DefaultRegion}");

            if (queryParams != null)
            {
                foreach (KeyValuePair<string, string?> kv in queryParams)
                {
                    if (!string.IsNullOrWhiteSpace(kv.Value))
                        parts.Add($"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value!)}");
                }
            }

            (string apiKey, string readToken) = Credentials;

            if (string.IsNullOrWhiteSpace(readToken) && !string.IsNullOrWhiteSpace(apiKey))
                parts.Add($"api_key={Uri.EscapeDataString(apiKey)}");

            // Strip the leading slash so the path resolves relative to the "/3" base.
            string relativePath = path.StartsWith('/') ? path[1..] : path;
            string url = $"{relativePath}{(relativePath.Contains('?') ? '&' : '?')}{string.Join('&', parts)}";

            // Simple in-memory response cache (not Blazor-level) so reopening dialogs is instant.
            if (_cache.TryGetValue(url, out CacheEntry? cached) && cached.ExpiresAt > DateTime.UtcNow)
            {
                return JsonSerializer.Deserialize<T>(cached.Json, JsonOpts);
            }

            using HttpRequestMessage request = new(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(readToken))
                request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {readToken}");

            using HttpResponseMessage response = await _http.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;
            if (!response.IsSuccessStatusCode)
            {
                _logger?.LogWarning("TMDB request {Path} failed with {Status}", path, (int)response.StatusCode);
                return null;
            }

            string json = await response.Content.ReadAsStringAsync(ct);
            Cache(url, json);
            return JsonSerializer.Deserialize<T>(json, JsonOpts);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "TMDB request {Path} threw", path);
            return null;
        }
    }

    void Cache(string url, string json)
    {
        if (_cache.Count >= MaxCacheEntries)
        {
            DateTime cutoff = DateTime.UtcNow;
            foreach (KeyValuePair<string, CacheEntry> entry in _cache)
            {
                if (entry.Value.ExpiresAt < cutoff)
                    _cache.TryRemove(entry.Key, out _);
            }
            // Still full after pruning expired entries: drop everything rather than grow unbounded.
            if (_cache.Count >= MaxCacheEntries)
                _cache.Clear();
        }

        _cache[url] = new CacheEntry(DateTime.UtcNow + CacheTtl, json);
    }
}

//  Season and episode DTOs used by the series detail dialog

public class TmdbTvShow
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("original_name")]
    public string OriginalName { get; set; } = "";

    [JsonPropertyName("overview")]
    public string Overview { get; set; } = "";

    [JsonPropertyName("poster_path")]
    public string? PosterPath { get; set; }

    [JsonPropertyName("backdrop_path")]
    public string? BackdropPath { get; set; }

    [JsonPropertyName("vote_average")]
    public double VoteAverage { get; set; }

    [JsonPropertyName("vote_count")]
    public int VoteCount { get; set; }

    [JsonPropertyName("number_of_seasons")]
    public int NumberOfSeasons { get; set; }

    [JsonPropertyName("number_of_episodes")]
    public int NumberOfEpisodes { get; set; }

    [JsonPropertyName("first_air_date")]
    public string? FirstAirDate { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("genres")]
    public List<string> Genres { get; set; } = [];

    [JsonPropertyName("origin_country")]
    public List<string> OriginCountry { get; set; } = [];

    [JsonPropertyName("networks")]
    public List<string> Networks { get; set; } = [];
}

public class TmdbTvSeason
{
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

    [JsonPropertyName("episodes")]
    public List<TmdbTvEpisode> Episodes { get; set; } = [];
}

public class TmdbTvEpisode
{
    [JsonPropertyName("episode_number")]
    public int EpisodeNumber { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("overview")]
    public string Overview { get; set; } = "";

    [JsonPropertyName("still_path")]
    public string? StillPath { get; set; }

    [JsonPropertyName("vote_average")]
    public double VoteAverage { get; set; }

    [JsonPropertyName("vote_count")]
    public int VoteCount { get; set; }

    [JsonPropertyName("air_date")]
    public string? AirDate { get; set; }

    [JsonPropertyName("runtime")]
    public int? Runtime { get; set; }
}

public class TmdbMovie
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("original_title")]
    public string OriginalTitle { get; set; } = "";

    [JsonPropertyName("overview")]
    public string Overview { get; set; } = "";

    [JsonPropertyName("poster_path")]
    public string? PosterPath { get; set; }

    [JsonPropertyName("backdrop_path")]
    public string? BackdropPath { get; set; }

    [JsonPropertyName("vote_average")]
    public double VoteAverage { get; set; }

    [JsonPropertyName("vote_count")]
    public int VoteCount { get; set; }

    [JsonPropertyName("runtime")]
    public int? Runtime { get; set; }

    [JsonPropertyName("release_date")]
    public string? ReleaseDate { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("genres")]
    public List<string> Genres { get; set; } = [];
}
