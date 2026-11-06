using Arrk.Ui.Components;
using Microsoft.Extensions.Logging;
using Arrk.Tvarr.Components.Shared;
using Arrk.Core.Models;
using Arrk.Tvarr.Clients;
using Arrk.Tvarr.Services;

namespace Arrk.Tvarr.Services;

/// <summary>
/// Top-level browse sections for the discover page.
/// </summary>
public enum MediaSection
{
    Movies,
    Shows,
    Anime,
    Music
}

/// <summary>Curated rows/categories available per section.</summary>
public enum DiscoverCategory
{
    Trending,
    Popular,
    TopRated,
    Upcoming,
    NowPlaying,
    OnTheAir,
    AiringToday,
    NewReleases
}

/// <summary>
/// The preferred metadata/search layer. Uses TMDB when configured and falls back to
/// Radarr/Sonarr lookups otherwise. Library membership is always resolved against
/// Radarr/Sonarr so remote results can be clearly distinguished from owned media.
/// </summary>
public partial class MediaCatalogService : ITvCatalog
{
    static readonly TimeSpan LibraryCacheTtl = TimeSpan.FromSeconds(20);

    const int AnimationGenreId = 16;
    const int KidsGenreId = 10762;

    // Anime is East-Asian animation only, this keeps US/UK cartoons out.
    static readonly HashSet<string> AnimeCountries = new(StringComparer.OrdinalIgnoreCase) { "JP", "CN", "KR", "TW", "HK" };
    static readonly HashSet<string> AnimeLanguages = new(StringComparer.OrdinalIgnoreCase) { "ja", "zh", "ko" };

    readonly TmdbService _tmdb;
    readonly RadarrService _radarr;
    readonly SonarrService _sonarr;
    readonly ILogger<MediaCatalogService> _logger;

    readonly Lock _gate = new();
    DateTime _movieCacheAt = DateTime.MinValue;
    Dictionary<int, TvMovie> _movieLibrary = [];
    DateTime _seriesCacheAt = DateTime.MinValue;
    Dictionary<int, TvSeries> _seriesByTmdb = [];
    Dictionary<int, TvSeries> _seriesByTvdb = [];

    readonly Dictionary<int, string> _movieGenres = [];
    readonly Dictionary<int, string> _tvGenres = [];
    readonly Dictionary<int, int> _tvdbIdCache = [];
    readonly Dictionary<int, string> _logoCache = [];

    // "Not Released" lookups do one lightweight TMDB detail call per remote movie; cache them so
    // re-rendering a grid (or paging back) never re-fetches.
    readonly Dictionary<int, (bool NotReleased, DateTime At)> _releaseCache = [];
    static readonly TimeSpan ReleaseCacheTtl = TimeSpan.FromHours(2);

    // Cards preload their title logo alongside the poster; cap concurrent TMDB lookups so a full
    // grid doesn't fire dozens of requests at once.
    static readonly SemaphoreSlim LogoFetchGate = new(6);

    public MediaCatalogService(
        TmdbService tmdb,
        RadarrService radarr,
        SonarrService sonarr,
        ILogger<MediaCatalogService> logger)
    {
        _tmdb = tmdb;
        _radarr = radarr;
        _sonarr = sonarr;
        _logger = logger;
    }

    public bool IsTmdbConfigured => _tmdb.IsConfigured;

    //  Library membership

    public async Task<Dictionary<int, TvMovie>> GetLibraryMoviesAsync(bool force = false, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!force && DateTime.UtcNow - _movieCacheAt < LibraryCacheTtl)
                return new Dictionary<int, TvMovie>(_movieLibrary);
        }

        Dictionary<int, TvMovie> map = [];
        try
        {
            List<TvMovie> movies = await _radarr.GetAllMovies(ct);
            foreach (TvMovie movie in movies)
            {
                if (movie.TmdbId > 0)
                    map[movie.TmdbId] = movie;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not load Radarr library for membership check");
        }

        lock (_gate)
        {
            _movieLibrary = map;
            _movieCacheAt = DateTime.UtcNow;
            return new Dictionary<int, TvMovie>(_movieLibrary);
        }
    }

    public async Task<(Dictionary<int, TvSeries> ByTmdb, Dictionary<int, TvSeries> ByTvdb)> GetLibrarySeriesAsync(bool force = false, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!force && DateTime.UtcNow - _seriesCacheAt < LibraryCacheTtl)
                return (new Dictionary<int, TvSeries>(_seriesByTmdb), new Dictionary<int, TvSeries>(_seriesByTvdb));
        }

        Dictionary<int, TvSeries> byTmdb = [];
        Dictionary<int, TvSeries> byTvdb = [];
        try
        {
            List<TvSeries> series = await _sonarr.GetAllSeries(ct);
            foreach (TvSeries s in series)
            {
                if (s.TmdbId > 0) byTmdb[s.TmdbId] = s;
                if (s.TvdbId > 0) byTvdb[s.TvdbId] = s;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not load Sonarr library for membership check");
        }

        lock (_gate)
        {
            _seriesByTmdb = byTmdb;
            _seriesByTvdb = byTvdb;
            _seriesCacheAt = DateTime.UtcNow;
            return (new Dictionary<int, TvSeries>(_seriesByTmdb), new Dictionary<int, TvSeries>(_seriesByTvdb));
        }
    }

    /// <summary>Returns the full Radarr library entry for a TMDB id, if present.</summary>
    public async Task<TvMovie?> GetLibraryMovieAsync(int tmdbId, CancellationToken ct = default)
    {
        Dictionary<int, TvMovie> map = await GetLibraryMoviesAsync(ct: ct);
        return map.TryGetValue(tmdbId, out TvMovie? movie) ? movie : null;
    }

    /// <summary>Returns the full Sonarr library entry for a TMDB id, if present.</summary>
    public async Task<TvSeries?> GetLibrarySeriesAsync(int tmdbId, CancellationToken ct = default)
    {
        (Dictionary<int, TvSeries> byTmdb, Dictionary<int, TvSeries> byTvdb) = await GetLibrarySeriesAsync(ct: ct);
        if (byTmdb.TryGetValue(tmdbId, out TvSeries? series)) return series;
        int tvdbId = await ResolveTvdbIdAsync(tmdbId, ct);
        return tvdbId > 0 && byTvdb.TryGetValue(tvdbId, out TvSeries? byTvdbSeries) ? byTvdbSeries : null;
    }

    public void InvalidateLibraryCache()
    {
        lock (_gate)
        {
            _movieCacheAt = DateTime.MinValue;
            _seriesCacheAt = DateTime.MinValue;
        }
    }

    //  Genres

    public async Task<List<TmdbGenre>> GetGenresAsync(MediaSection section, CancellationToken ct = default)
    {
        string type = section == MediaSection.Movies ? TmdbMediaType.Movie : TmdbMediaType.Tv;
        List<TmdbGenre> genres = await _tmdb.GetGenresAsync(type, ct);
        Dictionary<int, string> map = section == MediaSection.Movies ? _movieGenres : _tvGenres;
        lock (_gate)
        {
            foreach (TmdbGenre genre in genres)
                map[genre.Id] = genre.Name;
        }
        return genres;
    }

    List<string> ResolveGenreNames(IEnumerable<int> genreIds, bool isTv)
    {
        Dictionary<int, string> map = isTv ? _tvGenres : _movieGenres;
        List<string> names = [];
        lock (_gate)
        {
            foreach (int id in genreIds)
            {
                if (map.TryGetValue(id, out string? name))
                    names.Add(name);
            }
        }
        return names;
    }

    //  Search

    public async Task<List<MediaItem>> SearchAsync(MediaSection section, string query, int page = 1, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        if (section == MediaSection.Movies)
            return await SearchMoviesAsync(query, page, ct);
        if (section == MediaSection.Anime)
            return await SearchAnimeAsync(query, page, ct);
        return await SearchShowsAsync(query, page, ct);
    }

    /// <summary>Anime-only search. The Shows section includes both.</summary>
    public async Task<List<MediaItem>> SearchAnimeAsync(string query, int page = 1, CancellationToken ct = default)
    {
        if (_tmdb.IsConfigured)
        {
            await EnsureGenresAsync(MediaSection.Anime, ct);
            List<TmdbSearchResult> results = await _tmdb.SearchTvAsync(query, page, ct: ct);
            (Dictionary<int, TvSeries> byTmdb, Dictionary<int, TvSeries> _) = await GetLibrarySeriesAsync(ct: ct);
            return results
                .Where(IsAnimeResult)
                .Select(r => MapShow(r, byTmdb.GetValueOrDefault(r.Id), isAnime: true))
                .ToList();
        }

        try
        {
            List<SeriesLookupDto> results = await _sonarr.SearchSeries(query, ct);
            (Dictionary<int, TvSeries> byTmdb, Dictionary<int, TvSeries> byTvdb) = await GetLibrarySeriesAsync(ct: ct);
            return results
                .Where(r => r.Genres.Any(g => g.Contains("Animation", StringComparison.OrdinalIgnoreCase)))
                .Select(r => MediaItem.FromSeriesLookup(r, byTmdb.ContainsKey(r.TmdbId) || byTvdb.ContainsKey(r.TvdbId)))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Fallback anime search failed");
            return [];
        }
    }

    static bool IsAnimeResult(TmdbSearchResult r)
        => IsAnimeTitle(r.GenreIds, r.OriginCountry, r.OriginalLanguage);

    /// <summary>
    /// True when a TMDB title is anime: animation that originates in East Asia. A US/UK/French
    /// cartoon is animation but not anime, so origin decides whenever TMDB provides it. TMDB
    /// omits <c>origin_country</c> from movie list/search payloads, so in that case (and only
    /// then) fall back to the original language to keep Western cartoons out.
    /// </summary>
    static bool IsAnimeTitle(IEnumerable<int> genreIds, IReadOnlyCollection<string> originCountry, string originalLanguage)
    {
        if (!genreIds.Contains(AnimationGenreId))
            return false;
        if (originCountry.Any(c => AnimeCountries.Contains(c)))
            return true;
        return originCountry.Count == 0 && AnimeLanguages.Contains(originalLanguage);
    }
    /// <summary>TMDB recommendations ("More like this") mapped with library membership.</summary>
    public async Task<List<MediaItem>> GetRecommendationsAsync(string mediaType, int tmdbId, CancellationToken ct = default)
    {
        if (!_tmdb.IsConfigured || tmdbId <= 0)
            return [];

        bool isTv = string.Equals(mediaType, TmdbMediaType.Tv, StringComparison.OrdinalIgnoreCase);
        await EnsureGenresAsync(isTv ? MediaSection.Shows : MediaSection.Movies, ct);

        List<TmdbSearchResult> results = isTv
            ? await _tmdb.GetTvRecommendationsAsync(tmdbId, 1, ct)
            : await _tmdb.GetMovieRecommendationsAsync(tmdbId, 1, ct);

        return await MapManyAsync(results.Take(20).ToList(), isTv, isAnime: false, ct);
    }

    public async Task<List<MediaItem>> SearchMoviesAsync(string query, int page = 1, CancellationToken ct = default)
    {
        if (_tmdb.IsConfigured)
        {
            await EnsureGenresAsync(MediaSection.Movies, ct);
            List<TmdbSearchResult> results = await _tmdb.SearchMoviesAsync(query, page, ct: ct);
            Dictionary<int, TvMovie> library = await GetLibraryMoviesAsync(ct: ct);
            List<TmdbSearchResult> movies = results
                .Where(r => r.MediaType is null or TmdbMediaType.Movie)
                .ToList();
            return await MapMoviesAsync(movies, library, ct);
        }

        return await FallbackMovieSearchAsync(query, ct);
    }

    public async Task<List<MediaItem>> SearchShowsAsync(string query, int page = 1, CancellationToken ct = default)
    {
        if (_tmdb.IsConfigured)
        {
            await EnsureGenresAsync(MediaSection.Shows, ct);
            List<TmdbSearchResult> results = await _tmdb.SearchTvAsync(query, page, ct: ct);
            (Dictionary<int, TvSeries> byTmdb, Dictionary<int, TvSeries> byTvdb) = await GetLibrarySeriesAsync(ct: ct);
            return results
                .Where(r => r.MediaType is null or TmdbMediaType.Tv)
                .Select(r => MapShow(r, byTmdb.GetValueOrDefault(r.Id)))
                .ToList();
        }

        return await FallbackShowSearchAsync(query, ct);
    }

    async Task<List<MediaItem>> FallbackMovieSearchAsync(string query, CancellationToken ct)
    {
        try
        {
            List<MovieLookupDto> results = await _radarr.SearchMovie(query, ct);
            Dictionary<int, TvMovie> library = await GetLibraryMoviesAsync(ct: ct);
            return results.Select(r => MediaItem.FromMovieLookup(r, library.ContainsKey(r.TmdbId))).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Fallback movie search failed");
            return [];
        }
    }

    async Task<List<MediaItem>> FallbackShowSearchAsync(string query, CancellationToken ct)
    {
        try
        {
            List<SeriesLookupDto> results = await _sonarr.SearchSeries(query, ct);
            (Dictionary<int, TvSeries> byTmdb, Dictionary<int, TvSeries> byTvdb) = await GetLibrarySeriesAsync(ct: ct);
            return results
                .Select(r => MediaItem.FromSeriesLookup(r, byTmdb.ContainsKey(r.TmdbId) || byTvdb.ContainsKey(r.TvdbId)))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Fallback show search failed");
            return [];
        }
    }

    //  Discover / curated

    public async Task<List<MediaItem>> GetCategoryAsync(MediaSection section, DiscoverCategory category, int page = 1, CancellationToken ct = default)
    {
        if (!_tmdb.IsConfigured)
            return [];

        if (section == MediaSection.Anime)
            return await GetAnimeAsync(category, page, ct);

        if (section == MediaSection.Movies)
        {
            await EnsureGenresAsync(MediaSection.Movies, ct);
            Dictionary<int, TvMovie> library = await GetLibraryMoviesAsync(ct: ct);
            List<TmdbSearchResult> results = category switch
            {
                DiscoverCategory.Trending => await _tmdb.GetTrendingAsync(TmdbMediaType.Movie, TmdbWindow.Week, page, ct),
                DiscoverCategory.TopRated => await _tmdb.GetListAsync(TmdbMediaType.Movie, "top_rated", page, ct),
                DiscoverCategory.Upcoming => await _tmdb.GetListAsync(TmdbMediaType.Movie, "upcoming", page, ct),
                DiscoverCategory.NowPlaying => await _tmdb.GetListAsync(TmdbMediaType.Movie, "now_playing", page, ct),
                DiscoverCategory.NewReleases => await _tmdb.DiscoverAsync(new TmdbDiscoverQuery
                {
                    MediaType = TmdbMediaType.Movie,
                    Page = page,
                    SortBy = "primary_release_date.desc",
                    ReleaseDateGte = DateTime.UtcNow.AddMonths(-3).ToString("yyyy-MM-dd"),
                    VoteCountGte = 5
                }, ct),
                _ => await _tmdb.GetListAsync(TmdbMediaType.Movie, "popular", page, ct)
            };
            return await MapMoviesAsync(results, library, ct);
        }

        // Shows
        await EnsureGenresAsync(MediaSection.Shows, ct);
        (Dictionary<int, TvSeries> seriesByTmdb, Dictionary<int, TvSeries> seriesByTvdb) = await GetLibrarySeriesAsync(ct: ct);
        List<TmdbSearchResult> showResults = category switch
        {
            DiscoverCategory.Trending => await _tmdb.GetTrendingAsync(TmdbMediaType.Tv, TmdbWindow.Week, page, ct),
            DiscoverCategory.TopRated => await _tmdb.GetListAsync(TmdbMediaType.Tv, "top_rated", page, ct),
            DiscoverCategory.OnTheAir => await _tmdb.GetListAsync(TmdbMediaType.Tv, "on_the_air", page, ct),
            DiscoverCategory.AiringToday => await _tmdb.GetListAsync(TmdbMediaType.Tv, "airing_today", page, ct),
            DiscoverCategory.NewReleases => await _tmdb.DiscoverAsync(new TmdbDiscoverQuery
            {
                MediaType = TmdbMediaType.Tv,
                Page = page,
                SortBy = "first_air_date.desc",
                FirstAirDateGte = DateTime.UtcNow.AddMonths(-3).ToString("yyyy-MM-dd"),
                VoteCountGte = 5
            }, ct),
            _ => await _tmdb.GetListAsync(TmdbMediaType.Tv, "popular", page, ct)
        };
        return showResults
            .Select(r => MapShow(r, seriesByTmdb.GetValueOrDefault(r.Id)))
            .ToList();
    }

    /// <summary>Discover within a section with optional genre / year / sort filters.</summary>
    public async Task<List<MediaItem>> DiscoverAsync(MediaSection section, string? genreIds, string? sortBy, int? year, int page = 1, CancellationToken ct = default)
    {
        if (!_tmdb.IsConfigured)
            return [];

        bool isTv = section != MediaSection.Movies;
        await EnsureGenresAsync(section, ct);

        string? withGenres = genreIds;
        string? withoutGenres = null;
        string? origin = null;
        if (section == MediaSection.Anime)
        {
            withGenres = string.IsNullOrWhiteSpace(genreIds) ? AnimationGenreId.ToString() : $"{AnimationGenreId},{genreIds}";
            withoutGenres = KidsGenreId.ToString();
            origin = "JP|CN|KR|TW|HK";
        }

        TmdbDiscoverQuery query = new()
        {
            MediaType = isTv ? TmdbMediaType.Tv : TmdbMediaType.Movie,
            Page = page,
            SortBy = string.IsNullOrWhiteSpace(sortBy) ? "popularity.desc" : sortBy,
            WithGenres = withGenres,
            WithoutGenres = withoutGenres,
            WithOriginCountry = origin,
            Year = year,
            VoteCountGte = 5
        };

        List<TmdbSearchResult> results = await _tmdb.DiscoverAsync(query, ct);
        return await MapManyAsync(results, isTv, section == MediaSection.Anime, ct);
    }

    async Task<List<MediaItem>> GetAnimeAsync(DiscoverCategory category, int page, CancellationToken ct)
    {
        await EnsureGenresAsync(MediaSection.Anime, ct);
        (Dictionary<int, TvSeries> byTmdb, Dictionary<int, TvSeries> byTvdb) = await GetLibrarySeriesAsync(ct: ct);

        TmdbDiscoverQuery query = new()
        {
            MediaType = TmdbMediaType.Tv,
            Page = page,
            WithGenres = AnimationGenreId.ToString(),
            WithoutGenres = KidsGenreId.ToString(),
            WithOriginCountry = "JP|CN|KR|TW|HK",
            VoteCountGte = 10
        };

        switch (category)
        {
            case DiscoverCategory.Trending:
                query.SortBy = "popularity.desc";
                query.FirstAirDateGte = DateTime.UtcNow.AddYears(-2).ToString("yyyy-MM-dd");
                break;
            case DiscoverCategory.TopRated:
                query.SortBy = "vote_average.desc";
                query.VoteCountGte = 200;
                break;
            case DiscoverCategory.NewReleases:
                query.SortBy = "first_air_date.desc";
                query.FirstAirDateGte = DateTime.UtcNow.AddMonths(-6).ToString("yyyy-MM-dd");
                query.VoteCountGte = 0;
                break;
            case DiscoverCategory.AiringToday:
                query.SortBy = "popularity.desc";
                query.FirstAirDateLte = DateTime.UtcNow.ToString("yyyy-MM-dd");
                break;
            default:
                query.SortBy = "popularity.desc";
                break;
        }

        List<TmdbSearchResult> results = await _tmdb.DiscoverAsync(query, ct);
        return results.Select(r => MapShow(r, byTmdb.GetValueOrDefault(r.Id), isAnime: true)).ToList();
    }

    /// <summary>Anime for the current or requested broadcast season (Winter/Spring/Summer/Fall).</summary>
    public async Task<List<MediaItem>> GetAnimeSeasonalAsync(string? season, int? year, int page = 1, CancellationToken ct = default)
    {
        if (!_tmdb.IsConfigured)
            return [];

        (DateTime start, DateTime end, int resolvedYear) = ResolveSeason(season, year);
        await EnsureGenresAsync(MediaSection.Anime, ct);
        (Dictionary<int, TvSeries> byTmdb, Dictionary<int, TvSeries> byTvdb) = await GetLibrarySeriesAsync(ct: ct);

        List<TmdbSearchResult> results = await _tmdb.DiscoverAsync(new TmdbDiscoverQuery
        {
            MediaType = TmdbMediaType.Tv,
            Page = page,
            SortBy = "popularity.desc",
            WithGenres = AnimationGenreId.ToString(),
            WithoutGenres = KidsGenreId.ToString(),
            WithOriginCountry = "JP|CN|KR|TW|HK",
            FirstAirDateGte = start.ToString("yyyy-MM-dd"),
            FirstAirDateLte = end.ToString("yyyy-MM-dd"),
            VoteCountGte = 0
        }, ct);

        return results.Select(r => MapShow(r, byTmdb.GetValueOrDefault(r.Id), isAnime: true)).ToList();
    }

    static (DateTime Start, DateTime End, int Year) ResolveSeason(string? season, int? year)
    {
        DateTime now = DateTime.UtcNow;
        int resolvedYear = year ?? now.Year;
        string s = (season ?? CurrentSeason(now)).ToLowerInvariant();

        (int startMonth, int endMonth) = s switch
        {
            "winter" => (1, 3),
            "spring" => (4, 6),
            "summer" => (7, 9),
            _ => (10, 12)
        };

        DateTime start = new(resolvedYear, startMonth, 1);
        DateTime end = new(resolvedYear, endMonth, DateTime.DaysInMonth(resolvedYear, endMonth));
        return (start, end, resolvedYear);
    }

    public static string CurrentSeason(DateTime now) => now.Month switch
    {
        1 or 2 or 3 => "winter",
        4 or 5 or 6 => "spring",
        7 or 8 or 9 => "summer",
        _ => "fall"
    };

    //  Add-to-library resolution

    /// <summary>Fills in TVDB id, seasons and other metadata needed to add a show to Sonarr.</summary>
    public async Task<MediaItem> EnrichShowAsync(MediaItem item, CancellationToken ct = default)
    {
        if (item.TmdbId <= 0)
            return item;

        TmdbTvDetail? detail = await _tmdb.GetTvDetailAsync(item.TmdbId, ct);
        if (detail == null)
            return item;

        List<int> seasons = detail.Seasons
            .Where(s => s.SeasonNumber > 0 && s.EpisodeCount > 0)
            .Select(s => s.SeasonNumber)
            .OrderBy(n => n)
            .ToList();

        string status = detail.Status?.ToLowerInvariant() switch
        {
            "ended" => "Ended",
            "canceled" or "cancelled" => "Ended",
            "in production" or "planned" or "pilot" => "Upcoming",
            _ => "Continuing"
        };

        return item with
        {
            TvdbId = detail.ExternalIds?.TvdbId ?? item.TvdbId,
            ImdbId = detail.ExternalIds?.ImdbId ?? item.ImdbId,
            AvailableSeasonNumbers = seasons,
            SeasonCount = seasons.Count,
            Network = detail.Networks.FirstOrDefault()?.Name ?? item.Network,
            BackdropUrl = string.IsNullOrEmpty(item.BackdropUrl) ? TmdbService.GetBackdropUrl(detail.BackdropPath, "w780") : item.BackdropUrl,
            PosterUrl = string.IsNullOrEmpty(item.PosterUrl) ? TmdbService.GetPosterUrl(detail.PosterPath, "w500") : item.PosterUrl,
            Genres = detail.Genres.Count > 0 ? detail.Genres.Select(g => g.Name).ToList() : item.Genres,
            Rating = detail.VoteAverage > 0 ? detail.VoteAverage : item.Rating,
            Overview = string.IsNullOrWhiteSpace(item.Overview) ? detail.Overview : item.Overview,
            RuntimeMinutes = item.RuntimeMinutes ?? detail.Runtime,
            SeriesStatus = status,
            IsAnime = item.IsAnime || IsAnimeTitle(detail.Genres.Select(g => g.Id), detail.OriginCountry, detail.OriginalLanguage)
        };
    }

    /// <summary>Resolves a title logo URL for card hover artwork (cached).</summary>
    public async Task<string> GetLogoUrlAsync(string mediaType, int tmdbId, CancellationToken ct = default)
    {
        if (!_tmdb.IsConfigured || tmdbId <= 0)
            return "";

        lock (_gate)
        {
            if (_logoCache.TryGetValue(tmdbId, out string? cached))
                return cached;
        }

        List<TmdbImage> logos;
        await LogoFetchGate.WaitAsync(ct);
        try
        {
            logos = await _tmdb.GetLogosAsync(mediaType, tmdbId, ct);
        }
        finally
        {
            LogoFetchGate.Release();
        }

        string logo = TmdbService.FindBestLogo(logos);

        lock (_gate)
        {
            _logoCache[tmdbId] = logo;
        }
        return logo;
    }

    public async Task<int> ResolveTvdbIdAsync(int tmdbId, CancellationToken ct = default)
    {
        if (tmdbId <= 0) return 0;
        lock (_gate)
        {
            if (_tvdbIdCache.TryGetValue(tmdbId, out int cached))
                return cached;
        }

        TmdbTvDetail? detail = await _tmdb.GetTvDetailAsync(tmdbId, ct);
        int tvdbId = detail?.ExternalIds?.TvdbId ?? 0;
        if (tvdbId > 0)
        {
            lock (_gate)
            {
                _tvdbIdCache[tmdbId] = tvdbId;
            }
        }
        return tvdbId;
    }

    //  Mapping

    async Task<List<MediaItem>> MapManyAsync(
        List<TmdbSearchResult> results, bool isTv, bool isAnime, CancellationToken ct)
    {
        if (isTv)
        {
            (Dictionary<int, TvSeries> byTmdb, Dictionary<int, TvSeries> _) = await GetLibrarySeriesAsync(ct: ct);
            return results.Select(r => MapShow(r, byTmdb.GetValueOrDefault(r.Id), isAnime)).ToList();
        }

        Dictionary<int, TvMovie> library = await GetLibraryMoviesAsync(ct: ct);
        return await MapMoviesAsync(results, library, ct);
    }

    MediaItem MapMovie(TmdbSearchResult r, TvMovie? library, bool notYetReleased = false)
    {
        MediaItem item = library != null ? MediaItem.FromMovie(library) : new MediaItem();
        List<string> genres = ResolveGenreNames(r.GenreIds, isTv: false);
        return item with
        {
            Title = !string.IsNullOrWhiteSpace(r.DisplayTitle) ? r.DisplayTitle : item.Title,
            Year = r.Year > 0 ? r.Year : item.Year,
            Overview = !string.IsNullOrWhiteSpace(r.Overview) ? r.Overview : item.Overview,
            Rating = r.VoteAverage > 0 ? r.VoteAverage : item.Rating,
            PosterUrl = !string.IsNullOrEmpty(r.PosterPath) ? TmdbService.GetPosterUrl(r.PosterPath, "w500") : item.PosterUrl,
            BackdropUrl = TmdbService.GetBackdropUrl(r.BackdropPath, "w780"),
            Genres = genres.Count > 0 ? genres : item.Genres,
            IsInLibrary = library != null,
            IsSearchResult = library == null,
            TmdbId = r.Id,
            MediaType = TmdbMediaType.Movie,
            VoteCount = r.VoteCount,
            Popularity = r.Popularity,
            OriginalLanguage = r.OriginalLanguage,
            IsAnime = IsAnimeTitle(r.GenreIds, r.OriginCountry, r.OriginalLanguage),
            NotYetReleased = library != null ? item.NotYetReleased : notYetReleased
        };
    }

    /// <summary>Maps TMDB movie rows, resolving the "Not Released" badge for the remote ones.</summary>
    async Task<List<MediaItem>> MapMoviesAsync(List<TmdbSearchResult> results, Dictionary<int, TvMovie> library, CancellationToken ct)
    {
        Dictionary<int, bool> notReleased = await ResolveMovieReleaseAsync(results, library, ct);
        return results
            .Select(r => MapMovie(r, library.GetValueOrDefault(r.Id), notReleased.GetValueOrDefault(r.Id)))
            .ToList();
    }

    /// <summary>
    /// Resolves "Not Released" for remote movies via one lightweight TMDB lookup each. Only
    /// upcoming or recently released titles are looked up (a cinema-only film is by definition
    /// recent) and results are cached, so a grid never fires a request per card on every load.
    /// </summary>
    async Task<Dictionary<int, bool>> ResolveMovieReleaseAsync(List<TmdbSearchResult> results, Dictionary<int, TvMovie> library, CancellationToken ct)
    {
        DateTime cutoff = DateTime.UtcNow.AddMonths(-9);
        List<int> candidates = results
            .Where(r => r.Id > 0 && !library.ContainsKey(r.Id))
            .Where(r => r.MediaType is null or TmdbMediaType.Movie)
            .Where(r =>
            {
                DateTime? date = FormatHelper.ParseReleaseDate(r.ReleaseDate);
                return date is null || date.Value >= cutoff;
            })
            .Select(r => r.Id)
            .Distinct()
            .ToList();

        Dictionary<int, bool> resolved = [];
        if (candidates.Count == 0)
        {
            return resolved;
        }

        List<int> pending = [];
        lock (_gate)
        {
            foreach (int id in candidates)
            {
                if (_releaseCache.TryGetValue(id, out (bool NotReleased, DateTime At) entry)
                    && DateTime.UtcNow - entry.At < ReleaseCacheTtl)
                {
                    resolved[id] = entry.NotReleased;
                }
                else
                {
                    pending.Add(id);
                }
            }
        }

        using SemaphoreSlim gate = new(6);
        await Task.WhenAll(pending.Select(async id =>
        {
            await gate.WaitAsync(ct);
            try
            {
                bool notReleased = await _tmdb.IsMovieNotYetReleasedAsync(id, ct);
                lock (_gate)
                {
                    _releaseCache[id] = (notReleased, DateTime.UtcNow);
                    resolved[id] = notReleased;
                }
            }
            catch (OperationCanceledException)
            {
                // page navigated away; keep the unresolved entry out of the cache
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Release-status lookup failed for movie {TmdbId}", id);
            }
            finally
            {
                gate.Release();
            }
        }));

        return resolved;
    }

    MediaItem MapShow(TmdbSearchResult r, TvSeries? library, bool isAnime = false)
    {
        MediaItem item = library != null ? MediaItem.FromSeries(library) : new MediaItem();
        List<string> genres = ResolveGenreNames(r.GenreIds, isTv: true);
        return item with
        {
            Title = !string.IsNullOrWhiteSpace(r.DisplayTitle) ? r.DisplayTitle : item.Title,
            Year = r.Year > 0 ? r.Year : item.Year,
            Overview = !string.IsNullOrWhiteSpace(r.Overview) ? r.Overview : item.Overview,
            Rating = r.VoteAverage > 0 ? r.VoteAverage : item.Rating,
            PosterUrl = !string.IsNullOrEmpty(r.PosterPath) ? TmdbService.GetPosterUrl(r.PosterPath, "w500") : item.PosterUrl,
            BackdropUrl = TmdbService.GetBackdropUrl(r.BackdropPath, "w780"),
            Genres = genres.Count > 0 ? genres : item.Genres,
            IsInLibrary = library != null,
            IsSearchResult = library == null,
            TmdbId = r.Id,
            TvdbId = library?.TvdbId ?? 0,
            MediaType = TmdbMediaType.Tv,
            VoteCount = r.VoteCount,
            Popularity = r.Popularity,
            OriginalLanguage = r.OriginalLanguage,
            IsAnime = isAnime || IsAnimeResult(r),
            NotYetReleased = library != null
                ? item.NotYetReleased
                : ReleaseStatus.SeriesNotYetReleased(null, FormatHelper.ParseReleaseDate(r.FirstAirDate))
        };
    }

    async Task EnsureGenresAsync(MediaSection section, CancellationToken ct)
    {
        Dictionary<int, string> map = section == MediaSection.Movies ? _movieGenres : _tvGenres;
        lock (_gate)
        {
            if (map.Count > 0) return;
        }
        await GetGenresAsync(section, ct);
    }
}
