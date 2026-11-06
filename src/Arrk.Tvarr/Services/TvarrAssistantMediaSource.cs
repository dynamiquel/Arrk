using System.Globalization;
using Microsoft.Extensions.Logging;
using Arrk.Ai;
using Arrk.Core.Enums;
using Arrk.Core.Models;
using Arrk.Core.Playback;
using Arrk.Tvarr.Components.Shared;

namespace Arrk.Tvarr.Services;

/// <summary>
/// Exposes the TV/movie library and TMDB discovery to the assistant. Search is structured: text,
/// genre, person (cast/crew), year, anime, scope and sort. Local answers are filtered from the
/// cached Radarr/Sonarr library; discovery answers come from TMDB search, discover, trending and
/// person credits. The model only sees compact summaries and user-visible facts, never raw DTOs.
/// </summary>
public sealed class TvarrAssistantMediaSource(
    MediaCatalogService catalog,
    SonarrService sonarr,
    TmdbService tmdb,
    WatchStateService watchState,
    ILogger<TvarrAssistantMediaSource> logger) : IAssistantMediaSource
{
    /// <summary>Stable id prefix shared by the instance property and the static helpers.</summary>
    public const string SourceKey = "tv";

    static readonly string[] AnimeLanguages = ["ja", "zh", "ko", "cn", "tw", "hk"];
    static readonly string[] AnimeLanguageNames =
        ["japanese", "chinese", "korean", "mandarin", "cantonese", "taiwanese"];

    static readonly Dictionary<string, string> GenreAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sci fi"] = "science fiction",
        ["sci-fi"] = "science fiction",
        ["scifi"] = "science fiction",
        ["anime"] = "animation",
        ["cartoon"] = "animation",
        ["cartoons"] = "animation",
        ["rom-com"] = "romance",
        ["romcom"] = "romance",
        ["true crime"] = "crime"
    };

    public string Key => SourceKey;
    public string DisplayName => "Movies & TV";

    public bool IsAvailable => true;

    public async Task<IReadOnlyList<AssistantMediaSummary>> SearchAsync(
        AssistantMediaQuery query, CancellationToken ct)
    {
        if (!WantsTv(query.Domain))
            return [];

        // "anime" is a scope, not a TMDB genre. A model that sends it as the genre or the whole text
        // still gets Japanese animation rather than every Western cartoon.
        if (query.Anime != true && IsAnimeWord(query.Genre))
            query = query with { Anime = true, Genre = null };
        if (query.Anime != true && IsAnimeOnlyText(query.Text))
            query = query with { Anime = true, Text = "" };

        int limit = Math.Clamp(query.Limit <= 0 ? 8 : query.Limit, 1, 25);
        bool moviesWanted = WantsMovies(query.Domain);
        bool showsWanted = WantsShows(query.Domain);

        List<AssistantMediaSummary> items = [];

        // A person filter is resolved once and reused by both the library intersection and the
        // discovery candidate list. TMDB combined credits cover cast and crew.
        HashSet<int>? personIds = null;
        if (!string.IsNullOrWhiteSpace(query.Person))
            personIds = await ResolvePersonIdsAsync(query.Person, ct);

        // The library cache is cheap (20 s) and the discovery path needs it to mark or exclude items
        // that are already owned, so load it for every scope.
        Dictionary<int, TvMovie> emptyMovies = [];
        (Dictionary<int, TvSeries> ByTmdb, Dictionary<int, TvSeries> ByTvdb) emptyShows = ([], []);
        Dictionary<int, TvMovie> movies = moviesWanted
            ? await SafeAsync(() => catalog.GetLibraryMoviesAsync(false, ct), emptyMovies)
            : emptyMovies;
        (Dictionary<int, TvSeries> byTmdb, Dictionary<int, TvSeries> byTvdb) shows = showsWanted
            ? await SafeAsync(() => catalog.GetLibrarySeriesAsync(false, ct), emptyShows)
            : emptyShows;

        if (query.Scope is AssistantMediaScope.Library or AssistantMediaScope.Any)
        {
            // "Most trendy I have" means TMDB popularity, not just a local rating sort. The rank map
            // is bounded to two TMDB pages per domain and only fetched when the caller asked for it.
            Dictionary<int, int>? ranks = string.Equals(query.Sort, "popularity", StringComparison.OrdinalIgnoreCase)
                ? await PopularityRanksAsync(moviesWanted, showsWanted, query, ct)
                : null;

            if (moviesWanted)
            {
                IEnumerable<TvMovie> filtered = movies.Values
                    .Where(m => MatchesMovie(m, query, personIds));

                IEnumerable<TvMovie> ordered = ranks is null
                    ? SortLibrary(filtered, query.Sort)
                    : filtered.OrderBy(m => Rank(m.TmdbId, ranks))
                        .ThenByDescending(m => m.Rating ?? 0);

                foreach (TvMovie movie in ordered.Take(limit))
                    items.Add(ToSummary(movie));
            }

            if (showsWanted)
            {
                IEnumerable<TvSeries> filtered = shows.byTmdb.Values
                    .Where(s => MatchesSeries(s, query, personIds));

                IEnumerable<TvSeries> ordered = ranks is null
                    ? SortLibrary(filtered, query.Sort)
                    : filtered.OrderBy(s => Rank(s.TmdbId, ranks))
                        .ThenByDescending(s => s.Rating ?? 0);

                foreach (TvSeries series in ordered.Take(limit))
                    items.Add(ToSummary(series));
            }
        }

        // Library-only is the common "what do I have" question; do not dilute it with discovery.
        if (query.Scope == AssistantMediaScope.Library)
            return items.Take(limit).ToList();

        List<AssistantMediaSummary> discovered = await DiscoverAsync(
            query, moviesWanted, showsWanted, personIds, movies, shows, limit, ct);

        foreach (AssistantMediaSummary item in discovered)
        {
            if (items.Any(i => i.Id == item.Id))
                continue;
            items.Add(item);
            if (items.Count >= limit)
                break;
        }

        return items.Take(limit).ToList();
    }

    public async Task<AssistantMediaDetail?> GetAsync(string id, string? username, CancellationToken ct)
    {
        string[] parts = id.Split(':');
        if (parts.Length < 3 || !string.Equals(parts[0], Key, StringComparison.OrdinalIgnoreCase))
            return null;

        if (!int.TryParse(parts[2], out int tmdbId) || tmdbId <= 0)
            return null;

        return parts[1].ToLowerInvariant() switch
        {
            "movie" => await GetMovieAsync(tmdbId, username, ct),
            "series" => await GetSeriesAsync(tmdbId, username, ct),
            "season" when parts.Length >= 4 && int.TryParse(parts[3], out int season)
                => await GetSeasonAsync(tmdbId, season, ct),
            "episode" when parts.Length >= 5
                && int.TryParse(parts[3], out int epSeason)
                && int.TryParse(parts[4], out int episode)
                => await GetEpisodeAsync(tmdbId, epSeason, episode, ct),
            _ => null
        };
    }

    //  Library search

    static bool MatchesMovie(TvMovie movie, AssistantMediaQuery query, HashSet<int>? personIds)
    {
        if (query.Anime == true && !IsAnime(movie.Genres, movie.OriginalLanguage))
            return false;
        if (query.YearFrom is not null && movie.Year < query.YearFrom)
            return false;
        if (query.YearTo is not null && movie.Year > query.YearTo)
            return false;
        if (!MatchesGenre(movie.Genres, query.Genre))
            return false;
        if (personIds is not null
            && !personIds.Contains(movie.TmdbId))
        {
            return false;
        }

        return MatchesText(query.Text, movie.Title, movie.OriginalTitle, movie.Overview, movie.Genres);
    }

    static bool MatchesSeries(TvSeries series, AssistantMediaQuery query, HashSet<int>? personIds)
    {
        if (query.Anime == true && !IsAnime(series.Genres, series.OriginalLanguage))
            return false;
        if (query.YearFrom is not null && series.Year < query.YearFrom)
            return false;
        if (query.YearTo is not null && series.Year > query.YearTo)
            return false;
        if (!MatchesGenre(series.Genres, query.Genre))
            return false;
        if (personIds is not null
            && !personIds.Contains(series.TmdbId)
            && !personIds.Contains(series.TvdbId))
        {
            return false;
        }

        return MatchesText(query.Text, series.Title, series.OriginalTitle, series.Overview, series.Genres);
    }

    static IEnumerable<TvMovie> SortLibrary(IEnumerable<TvMovie> movies, string? sort) =>
        sort?.Trim().ToLowerInvariant() switch
        {
            "rating" => movies.OrderByDescending(m => m.Rating ?? 0).ThenByDescending(m => m.Year),
            "newest" or "year" => movies.OrderByDescending(m => m.Year).ThenByDescending(m => m.Rating ?? 0),
            "title" => movies.OrderBy(m => m.Title, StringComparer.OrdinalIgnoreCase),
            _ => movies.OrderByDescending(m => m.Rating ?? 0).ThenByDescending(m => m.Year)
        };

    static IEnumerable<TvSeries> SortLibrary(IEnumerable<TvSeries> series, string? sort) =>
        sort?.Trim().ToLowerInvariant() switch
        {
            "rating" => series.OrderByDescending(s => s.Rating ?? 0).ThenByDescending(s => s.Year),
            "newest" or "year" => series.OrderByDescending(s => s.Year).ThenByDescending(s => s.Rating ?? 0),
            "title" => series.OrderBy(s => s.Title, StringComparer.OrdinalIgnoreCase),
            _ => series.OrderByDescending(s => s.Rating ?? 0).ThenByDescending(s => s.Year)
        };

    async Task<Dictionary<int, int>> PopularityRanksAsync(
        bool moviesWanted, bool showsWanted, AssistantMediaQuery query, CancellationToken ct)
    {
        Dictionary<int, int> ranks = [];
        if (!tmdb.IsConfigured)
            return ranks;

        List<MediaItem> candidates = [];

        if (query.Anime == true)
        {
            candidates.AddRange(await SafeAsync(
                () => catalog.GetCategoryAsync(MediaSection.Anime, DiscoverCategory.Trending, 1, ct), []));
            candidates.AddRange(await SafeAsync(
                () => catalog.GetCategoryAsync(MediaSection.Anime, DiscoverCategory.TopRated, 1, ct), []));
        }
        else
        {
            if (moviesWanted)
            {
                string? genreIds = await ResolveGenreIdsAsync(MediaSection.Movies, query.Genre, ct);
                candidates.AddRange(await SafeAsync(
                    () => catalog.DiscoverAsync(MediaSection.Movies, genreIds, "popularity.desc", null, 1, ct), []));
                candidates.AddRange(await SafeAsync(
                    () => catalog.GetCategoryAsync(MediaSection.Movies, DiscoverCategory.Trending, 1, ct), []));
            }

            if (showsWanted)
            {
                string? genreIds = await ResolveGenreIdsAsync(MediaSection.Shows, query.Genre, ct);
                candidates.AddRange(await SafeAsync(
                    () => catalog.DiscoverAsync(MediaSection.Shows, genreIds, "popularity.desc", null, 1, ct), []));
                candidates.AddRange(await SafeAsync(
                    () => catalog.GetCategoryAsync(MediaSection.Shows, DiscoverCategory.Trending, 1, ct), []));
            }
        }

        int rank = 0;
        foreach (MediaItem item in candidates)
        {
            if (item.TmdbId > 0 && ranks.TryAdd(item.TmdbId, rank))
                rank++;
        }

        return ranks;
    }

    static int Rank(int tmdbId, IReadOnlyDictionary<int, int> ranks) =>
        ranks.TryGetValue(tmdbId, out int rank) ? rank : int.MaxValue;


    //  Discovery search

    async Task<List<AssistantMediaSummary>> DiscoverAsync(
        AssistantMediaQuery query,
        bool moviesWanted,
        bool showsWanted,
        HashSet<int>? personIds,
        Dictionary<int, TvMovie> libraryMovies,
        (Dictionary<int, TvSeries> ByTmdb, Dictionary<int, TvSeries> ByTvdb) libraryShows,
        int limit,
        CancellationToken ct)
    {
        List<AssistantMediaSummary> results = [];

        if (personIds is { Count: > 0 })
        {
            results = await PersonCreditsAsync(
                query, moviesWanted, showsWanted, personIds, libraryMovies, libraryShows, limit, ct);
            if (results.Count > 0)
                return results;
        }

        if (query.Anime == true && showsWanted)
            return await AnimeDiscoveryAsync(query, limit, ct);

        if (!string.IsNullOrWhiteSpace(query.Text))
        {
            if (moviesWanted)
            {
                List<MediaItem> movies = await SafeSearchAsync(MediaSection.Movies, query.Text, ct);
                results.AddRange(movies.Select(m => ToSummary(m, isSeries: false)));
            }

            if (showsWanted)
            {
                List<MediaItem> shows = await SafeSearchAsync(
                    query.Anime == true ? MediaSection.Anime : MediaSection.Shows, query.Text, ct);
                results.AddRange(shows.Select(m => ToSummary(m, isSeries: true)));
            }
        }
        else
        {
            string? genreIds = await ResolveGenreIdsAsync(
                query.Anime == true ? MediaSection.Anime : MediaSection.Movies, query.Genre, ct);

            if (moviesWanted && query.Anime != true)
            {
                List<MediaItem> movies = await SafeAsync(
                    () => catalog.DiscoverAsync(MediaSection.Movies, genreIds,
                        SortParam(query.Sort, movie: true), query.YearFrom, 1, ct), []);
                results.AddRange(movies.Select(m => ToSummary(m, isSeries: false)));
            }

            if (showsWanted)
            {
                List<MediaItem> shows = await SafeAsync(
                    () => catalog.DiscoverAsync(MediaSection.Shows, genreIds,
                        SortParam(query.Sort, movie: false), query.YearFrom, 1, ct), []);
                results.AddRange(shows.Select(m => ToSummary(m, isSeries: true)));
            }
        }

        if (query.Scope == AssistantMediaScope.Discovery)
            results = results.Where(r => r.Status == "Not in library").ToList();

        if (query.YearTo is not null)
            results = results.Where(r => r.Year is null || r.Year <= query.YearTo).ToList();
        if (query.YearFrom is not null)
            results = results.Where(r => r.Year is null || r.Year >= query.YearFrom).ToList();

        return Results(results, query.Sort, limit);
    }

    async Task<List<AssistantMediaSummary>> AnimeDiscoveryAsync(
        AssistantMediaQuery query, int limit, CancellationToken ct)
    {
        List<MediaItem> seasonal = await SafeAsync(
            () => catalog.GetAnimeSeasonalAsync(null, query.YearFrom, 1, ct), []);
        List<MediaItem> trending = await SafeAsync(
            () => catalog.GetCategoryAsync(MediaSection.Anime, DiscoverCategory.Trending, 1, ct), []);

        IEnumerable<MediaItem> merged = seasonal.Concat(trending);
        if (query.YearTo is not null)
            merged = merged.Where(m => m.Year == 0 || m.Year <= query.YearTo);

        if (!string.IsNullOrWhiteSpace(query.Genre) && !IsAnimeGenre(query.Genre))
        {
            string genre = NormalizeGenre(query.Genre)!;
            merged = merged.Where(m => m.Genres.Any(g =>
                string.Equals(g, genre, StringComparison.OrdinalIgnoreCase)));
        }

        List<AssistantMediaSummary> results = merged
            .Select(m => ToSummary(m, isSeries: true))
            .Where(m => m.Status == "Not in library")
            .ToList();

        return Results(results, query.Sort ?? "rating", limit);
    }

    async Task<List<AssistantMediaSummary>> PersonCreditsAsync(
        AssistantMediaQuery query,
        bool moviesWanted,
        bool showsWanted,
        HashSet<int> personIds,
        Dictionary<int, TvMovie> libraryMovies,
        (Dictionary<int, TvSeries> ByTmdb, Dictionary<int, TvSeries> ByTvdb) libraryShows,
        int limit,
        CancellationToken ct)
    {
        List<TmdbSearchResult> credits = [];
        foreach (int personId in personIds)
        {
            TmdbPersonCredits? result = await SafeAsync(
                () => tmdb.GetPersonCreditsAsync(personId, ct));
            if (result is null)
                continue;
            credits.AddRange(result.Cast);
            credits.AddRange(result.Crew);
        }

        // One person is credited many times; dedupe before filtering so a film with several roles
        // in the credits list does not appear twice.
        IEnumerable<TmdbSearchResult> candidates = credits
            .Where(c => !string.IsNullOrWhiteSpace(c.DisplayTitle))
            .GroupBy(c => (c.Id, c.IsTv))
            .Select(g => g.First())
            .Where(c => c.IsTv ? showsWanted : moviesWanted)
            .Where(c => query.Anime != true || IsAnime(c.GenreIds, c.OriginalLanguage, c.OriginCountry))
            .Where(c => MatchesText(query.Text, c.DisplayTitle, c.Overview, c.GenreIds))
            .Where(c => query.YearFrom is null || c.Year >= query.YearFrom)
            .Where(c => query.YearTo is null || c.Year <= query.YearTo);

        List<AssistantMediaSummary> ordered = SortDiscovery(candidates, query.Sort)
            .Take(Math.Max(limit * 2, 12))
            .Select(c => ToSummary(c, libraryMovies, libraryShows))
            .ToList();

        if (query.Scope == AssistantMediaScope.Discovery)
            ordered = ordered.Where(r => r.Status == "Not in library").ToList();

        return ordered.Take(limit).ToList();
    }

    static IEnumerable<TmdbSearchResult> SortDiscovery(IEnumerable<TmdbSearchResult> items, string? sort)
    {
        // Prefer released/older rows over unmade projects when the user asks for "newest".
        IEnumerable<TmdbSearchResult> released = items.Where(i => i.Year > 0);
        return sort?.Trim().ToLowerInvariant() switch
        {
            "newest" or "year" => released.OrderByDescending(i => i.Year),
            "rating" => released.OrderByDescending(i => i.VoteAverage).ThenByDescending(i => i.VoteCount),
            "title" => released.OrderBy(i => i.DisplayTitle, StringComparer.OrdinalIgnoreCase),
            _ => released.OrderByDescending(i => i.Popularity).ThenByDescending(i => i.VoteAverage)
        };
    }

    static List<AssistantMediaSummary> Results(
        IEnumerable<AssistantMediaSummary> items, string? sort, int limit)
    {
        IEnumerable<AssistantMediaSummary> ordered = items
            .GroupBy(i => i.Id)
            .Select(g => g.First());

        ordered = sort?.Trim().ToLowerInvariant() switch
        {
            "title" => ordered.OrderBy(i => i.Title, StringComparer.OrdinalIgnoreCase),
            "newest" => ordered.OrderByDescending(i => i.Year ?? 0),
            "year" => ordered.OrderByDescending(i => i.Year ?? 0),
            _ => ordered
        };

        return ordered.Take(limit).ToList();
    }

    //  Detail

    async Task<AssistantMediaDetail?> GetMovieAsync(int tmdbId, string? username, CancellationToken ct)
    {
        TvMovie? movie = await SafeAsync(() => catalog.GetLibraryMovieAsync(tmdbId, ct));
        if (movie is not null)
        {
            List<string> facts =
            [
                $"Library status: {(movie.HasFile ? "downloaded" : "missing")}",
                $"Monitored: {(movie.Monitored ? "yes" : "no")}",
                $"Year: {movie.Year}"
            ];
            AddIf(facts, "Runtime", movie.RuntimeMinutes is > 0 ? $"{movie.RuntimeMinutes} min" : null);
            AddIf(facts, "Quality", !string.IsNullOrWhiteSpace(movie.Quality.DisplayText) ? movie.Quality.DisplayText : null);
            AddIf(facts, "Rating", movie.Rating is > 0 ? movie.Rating.Value.ToString("0.0", CultureInfo.InvariantCulture) : null);
            AddIf(facts, "Genres", movie.Genres.Count > 0 ? string.Join(", ", movie.Genres) : null);
            AddIf(facts, "Size on disk", FormatSize(movie.SizeOnDisk));

            if (!string.IsNullOrWhiteSpace(username) && watchState.IsAvailable)
            {
                IReadOnlyDictionary<MediaKey, WatchState> states = await SafeAsync(() =>
                    watchState.GetStatesAsync(username, [new MediaKey(TvMediaDomains.Movie, tmdbId)], ct),
                    new Dictionary<MediaKey, WatchState>());
                if (states.TryGetValue(new MediaKey(TvMediaDomains.Movie, tmdbId), out WatchState? state))
                    facts.Add("Watch state: " + DescribeWatchState(state));
            }

            List<AssistantMediaSummary> related = await RecommendationsAsync("movie", tmdbId, ct);
            TmdbMovieDetail? detail = await SafeAsync(() => tmdb.GetMovieDetailAsync(tmdbId, ct));
            AddCastFacts(facts, detail?.Credits);

            AssistantMediaSummary summary = new(Key, $"tv:movie:{tmdbId}", AssistantMediaKind.Movie,
                movie.Title, "Movie", movie.Year > 0 ? movie.Year : null,
                movie.HasFile ? "Downloaded" : "Missing", movie.PosterUrl);
            return new AssistantMediaDetail(summary, movie.Overview, facts, related);
        }

        TmdbMovieDetail? remoteDetail = await SafeAsync(() => tmdb.GetMovieDetailAsync(tmdbId, ct));
        if (remoteDetail is null)
            return null;

        List<string> remoteFacts =
        [
            "Library status: not in the library"
        ];
        AddIf(remoteFacts, "Runtime", remoteDetail.Runtime is > 0 ? $"{remoteDetail.Runtime} min" : null);
        AddIf(remoteFacts, "Rating", remoteDetail.VoteAverage > 0
            ? $"{remoteDetail.VoteAverage.ToString("0.0", CultureInfo.InvariantCulture)} ({remoteDetail.VoteCount:N0} votes)"
            : null);
        AddIf(remoteFacts, "Genres", remoteDetail.Genres.Count > 0
            ? string.Join(", ", remoteDetail.Genres.Select(g => g.Name))
            : null);
        AddIf(remoteFacts, "Release status", string.IsNullOrWhiteSpace(remoteDetail.Status) ? null : remoteDetail.Status);
        AddIf(remoteFacts, "Release date", remoteDetail.ReleaseDate);
        AddCastFacts(remoteFacts, remoteDetail.Credits);

        AssistantMediaSummary remote = new(Key, $"tv:movie:{tmdbId}", AssistantMediaKind.Movie,
            remoteDetail.Title, "Movie", remoteDetail.Year > 0 ? remoteDetail.Year : null, "Not in library");
        return new AssistantMediaDetail(remote, remoteDetail.Overview, remoteFacts,
            await RecommendationsAsync("movie", tmdbId, ct));
    }

    async Task<AssistantMediaDetail?> GetSeriesAsync(int tmdbId, string? username, CancellationToken ct)
    {
        TvSeries? series = await SafeAsync(() => catalog.GetLibrarySeriesAsync(tmdbId, ct));
        if (series is null)
        {
            TmdbTvDetail? remote = await SafeAsync(() => tmdb.GetTvDetailAsync(tmdbId, ct));
            if (remote is null)
                return null;

            List<string> remoteFacts = ["Library status: not in the library"];
            AddIf(remoteFacts, "Seasons", remote.NumberOfSeasons > 0 ? remote.NumberOfSeasons.ToString() : null);
            AddIf(remoteFacts, "Episodes", remote.NumberOfEpisodes > 0 ? remote.NumberOfEpisodes.ToString() : null);
            AddIf(remoteFacts, "Rating", remote.VoteAverage > 0
                ? $"{remote.VoteAverage.ToString("0.0", CultureInfo.InvariantCulture)} ({remote.VoteCount:N0} votes)"
                : null);
            AddIf(remoteFacts, "Genres", remote.Genres.Count > 0
                ? string.Join(", ", remote.Genres.Select(g => g.Name))
                : null);
            AddIf(remoteFacts, "Status", remote.Status);
            AddCastFacts(remoteFacts, remote.Credits);

            AssistantMediaSummary remoteSummary = new(Key, $"tv:series:{tmdbId}", AssistantMediaKind.Series,
                remote.Name, "Series", ParseYear(remote.FirstAirDate), "Not in library");

            List<AssistantMediaSummary> remoteSeasons = remote.Seasons
                .Where(s => s.SeasonNumber > 0)
                .Select(s => new AssistantMediaSummary(SourceKey, $"tv:season:{tmdbId}:{s.SeasonNumber}",
                    AssistantMediaKind.Season, s.Name, "Season", ParseYear(s.AirDate),
                    s.EpisodeCount > 0 ? $"{s.EpisodeCount} episodes" : ""))
                .ToList();

            return new AssistantMediaDetail(remoteSummary, remote.Overview, remoteFacts, remoteSeasons);
        }

        List<string> facts =
        [
            $"Library status: {series.DownloadedEpisodes}/{series.TotalEpisodes} episodes downloaded",
            $"Monitored: {(series.Monitored ? "yes" : "no")}",
            $"Seasons: {series.Seasons.Count(s => s.SeasonNumber > 0)}"
        ];
        AddIf(facts, "Year", series.Year > 0 ? series.Year.ToString() : null);
        AddIf(facts, "Status", series.Status.ToString());
        AddIf(facts, "Network", string.IsNullOrWhiteSpace(series.Network) ? null : series.Network);
        AddIf(facts, "Genres", series.Genres.Count > 0 ? string.Join(", ", series.Genres) : null);
        AddIf(facts, "Rating", series.Rating is > 0
            ? series.Rating.Value.ToString("0.0", CultureInfo.InvariantCulture)
            : null);
        AddIf(facts, "Size on disk", FormatSize(series.SizeOnDisk ?? 0));

        if (!string.IsNullOrWhiteSpace(username) && watchState.IsAvailable)
        {
            IReadOnlyDictionary<int, SeriesWatchState> states = await SafeAsync(() =>
                watchState.GetSeriesStatesAsync(username, [tmdbId, series.TvdbId], ct),
                new Dictionary<int, SeriesWatchState>());
            SeriesWatchState? state = states.GetValueOrDefault(tmdbId) ?? states.GetValueOrDefault(series.TvdbId);
            if (state is not null)
            {
                string last = state.LastWatchedSeason is > 0
                    ? $" · last S{state.LastWatchedSeason}E{state.LastWatchedEpisode}"
                    : "";
                facts.Add($"Watch state: {state.WatchedEpisodes}/{state.TotalEpisodes} episodes watched{last}");
            }
        }

        TmdbTvDetail? showDetail = await SafeAsync(() => tmdb.GetTvDetailAsync(tmdbId, ct));
        AddCastFacts(facts, showDetail?.Credits);

        List<AssistantMediaSummary> seasons = series.Seasons
            .Where(s => s.SeasonNumber > 0)
            .OrderBy(s => s.SeasonNumber)
            .Select(s => new AssistantMediaSummary(SourceKey, $"tv:season:{tmdbId}:{s.SeasonNumber}",
                AssistantMediaKind.Season, $"Season {s.SeasonNumber}", "Season", null,
                $"{s.DownloadedEpisodeCount}/{Math.Max(s.TotalEpisodeCount, s.EpisodeCount)} downloaded"))
            .ToList();

        AssistantMediaSummary summary = new(Key, $"tv:series:{tmdbId}", AssistantMediaKind.Series,
            series.Title, "Series", series.Year > 0 ? series.Year : null,
            $"{series.DownloadedEpisodes}/{series.TotalEpisodes} episodes", series.PosterUrl);
        return new AssistantMediaDetail(summary, series.Overview, facts, seasons);
    }

    async Task<AssistantMediaDetail?> GetSeasonAsync(int tmdbId, int seasonNumber, CancellationToken ct)
    {
        TvSeries? series = await SafeAsync(() => catalog.GetLibrarySeriesAsync(tmdbId, ct));
        if (series is null)
            return null;

        TvSeason? season = series.Seasons.FirstOrDefault(s => s.SeasonNumber == seasonNumber);
        List<string> facts = [];
        if (season is not null)
        {
            facts.Add($"Downloaded: {season.DownloadedEpisodeCount}/{Math.Max(season.TotalEpisodeCount, season.EpisodeCount)} episodes");
            facts.Add($"Monitored: {(season.Monitored ? "yes" : "no")}");
            AddIf(facts, "Quality", season.Quality?.DisplayText);
            AddIf(facts, "Size on disk", FormatSize(season.SizeOnDisk));
        }
        else
        {
            facts.Add("This season is not tracked in the library.");
        }

        List<TvEpisode> episodes = await SafeAsync(
            () => sonarr.GetEpisodes(series.Id, ct), []);
        List<AssistantMediaSummary> related = episodes
            .Where(e => e.SeasonNumber == seasonNumber)
            .OrderBy(e => e.EpisodeNumber)
            .Select(e => new AssistantMediaSummary(SourceKey,
                $"tv:episode:{tmdbId}:{seasonNumber}:{e.EpisodeNumber}",
                AssistantMediaKind.Episode,
                $"S{seasonNumber:00}E{e.EpisodeNumber:00} · {e.Title}", e.Title,
                e.AirDate?.Year,
                e.HasFile ? "Downloaded" : e.Monitored ? "Missing" : "Not monitored",
                string.IsNullOrWhiteSpace(e.ImageUrl) ? null : e.ImageUrl))
            .ToList();

        AssistantMediaSummary summary = new(Key, $"tv:season:{tmdbId}:{seasonNumber}", AssistantMediaKind.Season,
            $"{series.Title} · Season {seasonNumber}", "Season", series.Year > 0 ? series.Year : null,
            related.Count(e => e.Status == "Downloaded") + "/" + related.Count + " downloaded",
            series.PosterUrl);
        return new AssistantMediaDetail(summary, "", facts, related);
    }

    async Task<AssistantMediaDetail?> GetEpisodeAsync(int tmdbId, int seasonNumber, int episodeNumber, CancellationToken ct)
    {
        TvSeries? series = await SafeAsync(() => catalog.GetLibrarySeriesAsync(tmdbId, ct));
        if (series is null)
            return null;

        List<TvEpisode> episodes = await SafeAsync(
            () => sonarr.GetEpisodes(series.Id, ct), []);
        TvEpisode? episode = episodes.FirstOrDefault(e =>
            e.SeasonNumber == seasonNumber && e.EpisodeNumber == episodeNumber);
        if (episode is null)
            return null;

        List<string> facts =
        [
            $"Downloaded: {(episode.HasFile ? "yes" : "no")}",
            $"Monitored: {(episode.Monitored ? "yes" : "no")}"
        ];
        AddIf(facts, "Aired", episode.AirDate?.ToString("yyyy-MM-dd"));
        AddIf(facts, "Quality", episode.Quality?.DisplayText);

        AssistantMediaSummary summary = new(Key,
            $"tv:episode:{tmdbId}:{seasonNumber}:{episodeNumber}", AssistantMediaKind.Episode,
            $"{series.Title} · S{seasonNumber:00}E{episodeNumber:00} · {episode.Title}",
            $"Series: {series.Title}", episode.AirDate?.Year,
            episode.HasFile ? "Downloaded" : episode.Monitored ? "Missing" : "Not monitored");
        return new AssistantMediaDetail(summary, episode.Overview, facts, []);
    }

    async Task<List<AssistantMediaSummary>> RecommendationsAsync(
        string mediaType, int tmdbId, CancellationToken ct)
    {
        List<MediaItem> items = await SafeAsync(
            () => catalog.GetRecommendationsAsync(mediaType, tmdbId, ct), []);
        return items
            .Where(i => i.TmdbId > 0)
            .Take(8)
            .Select(i => ToSummary(i, string.Equals(i.MediaType, "tv", StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    //  Mapping and filters

    async Task<List<MediaItem>> SafeSearchAsync(MediaSection section, string text, CancellationToken ct)
    {
        try
        {
            return await catalog.SearchAsync(section, text, 1, ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Assistant {Section} search failed", section);
            return [];
        }
    }

    async Task<string?> ResolveGenreIdsAsync(
        MediaSection section, string? genre, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(genre) || IsAnimeGenre(genre))
            return null;

        string? normalized = NormalizeGenre(genre);
        if (normalized is null)
            return null;

        List<TmdbGenre> genres = await SafeAsync(() => catalog.GetGenresAsync(section, ct), []);
        TmdbGenre? match = genres.FirstOrDefault(g =>
            string.Equals(g.Name, normalized, StringComparison.OrdinalIgnoreCase));
        return match is null ? null : match.Id.ToString(CultureInfo.InvariantCulture);
    }

    static string? NormalizeGenre(string? genre)
    {
        if (string.IsNullOrWhiteSpace(genre))
            return null;

        string value = genre.Trim().ToLowerInvariant();
        return GenreAliases.GetValueOrDefault(value, value);
    }

    static bool MatchesGenre(IReadOnlyList<string> genres, string? genre)
    {
        if (string.IsNullOrWhiteSpace(genre))
            return true;

        string? normalized = NormalizeGenre(genre);
        return normalized is not null && genres.Any(g =>
            string.Equals(g, normalized, StringComparison.OrdinalIgnoreCase));
    }

    static bool MatchesText(
        string? text, string title, string? originalTitle, string? overview, IReadOnlyList<string> genres) =>
        MatchesAllTokens(text, string.Join(' ',
            new[] { title, originalTitle ?? "", overview ?? "" }.Concat(genres)));

    static bool MatchesText(
        string? text, string title, string? overview, IReadOnlyList<int> genreIds) =>
        MatchesAllTokens(text, $"{title} {overview} {string.Join(' ', genreIds)}");

    /// <summary>
    /// Every token must appear. A single token also matches the punctuation-stripped haystack, so
    /// "rezero" finds "Re:Zero" without making a short token match dangerously inside other words.
    /// </summary>
    static bool MatchesAllTokens(string? text, string haystack)
    {
        if (string.IsNullOrWhiteSpace(text))
            return true;

        string compact = string.Concat(haystack.Where(char.IsLetterOrDigit));
        string[] tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        foreach (string token in tokens)
        {
            if (haystack.Contains(token, StringComparison.OrdinalIgnoreCase))
                continue;

            string compactToken = string.Concat(token.Where(char.IsLetterOrDigit));
            if (tokens.Length == 1 && compactToken.Length > 2
                && compact.Contains(compactToken, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return false;
        }

        return true;
    }

    static bool IsAnime(IReadOnlyList<string> genres, string? language) =>
        MatchesGenre(genres, "animation") && IsAnimeLanguage(language);

    static bool IsAnime(
        IReadOnlyList<int> genreIds, string? language, IReadOnlyList<string> originCountry) =>
        genreIds.Contains(16)
        && (IsAnimeLanguage(language)
            || originCountry.Any(c => AnimeLanguages.Contains(c, StringComparer.OrdinalIgnoreCase)));

    /// <summary>Radarr stores language display names ("Japanese"); TMDB returns codes ("ja").</summary>
    static bool IsAnimeLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
            return false;

        string value = language.Trim();
        return AnimeLanguages.Contains(value, StringComparer.OrdinalIgnoreCase)
            || AnimeLanguageNames.Contains(value, StringComparer.OrdinalIgnoreCase);
    }

    static bool IsAnimeOnlyText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        string[] tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return tokens.Length > 0 && tokens.All(t =>
            t.Equals("anime", StringComparison.OrdinalIgnoreCase)
            || t.Equals("animes", StringComparison.OrdinalIgnoreCase));
    }

    static bool IsAnimeWord(string? genre) =>
        !string.IsNullOrWhiteSpace(genre)
        && genre.Contains("anime", StringComparison.OrdinalIgnoreCase);

    static bool IsAnimeGenre(string? genre) =>
        !string.IsNullOrWhiteSpace(genre)
        && (genre.Contains("anime", StringComparison.OrdinalIgnoreCase)
            || genre.Contains("animation", StringComparison.OrdinalIgnoreCase)
            || genre.Contains("cartoon", StringComparison.OrdinalIgnoreCase));

    static string SortParam(string? sort, bool movie) =>
        sort?.Trim().ToLowerInvariant() switch
        {
            "rating" => "vote_average.desc",
            "newest" => movie ? "primary_release_date.desc" : "first_air_date.desc",
            "year" => movie ? "primary_release_date.desc" : "first_air_date.desc",
            "title" => "original_title.asc",
            _ => "popularity.desc"
        };

    async Task<HashSet<int>?> ResolvePersonIdsAsync(string person, CancellationToken ct)
    {
        List<TmdbSearchResult> people = await SafeAsync(
            () => tmdb.SearchPeopleAsync(person, 1, ct), []);
        if (people.Count == 0)
            return null;

        // Prefer an exact/normalised name match, then TMDB's order. A person search can return a
        // documentary about the actor before the actor, so the exact match matters.
        TmdbSearchResult? best = people.FirstOrDefault(p =>
                string.Equals(p.DisplayTitle.Trim(), person.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? people.First();

        HashSet<int> ids = [];
        TmdbPersonCredits? credits = await SafeAsync(() => tmdb.GetPersonCreditsAsync(best.Id, ct));
        if (credits is null)
            return null;

        foreach (TmdbSearchResult credit in credits.Cast.Concat(credits.Crew))
        {
            if (credit.Id > 0)
                ids.Add(credit.Id);
        }

        return ids.Count > 0 ? ids : null;
    }

    static AssistantMediaSummary ToSummary(TvMovie movie) => new(
        SourceKey, $"tv:movie:{movie.TmdbId}", AssistantMediaKind.Movie,
        movie.Title, "Movie", movie.Year > 0 ? movie.Year : null,
        movie.HasFile ? "Downloaded" : "In library", movie.PosterUrl);

    static AssistantMediaSummary ToSummary(TvSeries series) => new(
        SourceKey, $"tv:series:{series.TmdbId}", AssistantMediaKind.Series,
        series.Title, "Series", series.Year > 0 ? series.Year : null,
        series.DownloadedEpisodes > 0 ? "Downloaded" : "In library", series.PosterUrl);

    static AssistantMediaSummary ToSummary(MediaItem item, bool isSeries)
    {
        string subtitle = isSeries ? "Series" : "Movie";
        return new AssistantMediaSummary(SourceKey,
            isSeries ? $"{SourceKey}:series:{item.TmdbId}" : $"{SourceKey}:movie:{item.TmdbId}",
            isSeries ? AssistantMediaKind.Series : AssistantMediaKind.Movie,
            item.Title, subtitle, item.Year > 0 ? item.Year : null,
            string.IsNullOrWhiteSpace(item.LibraryStatus)
                ? (item.IsInLibrary ? "In library" : "Not in library")
                : item.LibraryStatus,
            string.IsNullOrWhiteSpace(item.PosterUrl) ? null : item.PosterUrl);
    }

    static AssistantMediaSummary ToSummary(
        TmdbSearchResult result,
        Dictionary<int, TvMovie> libraryMovies,
        (Dictionary<int, TvSeries> ByTmdb, Dictionary<int, TvSeries> ByTvdb) libraryShows)
    {
        string status = "Not in library";
        if (result.IsTv)
        {
            if (libraryShows.ByTmdb.TryGetValue(result.Id, out TvSeries? series))
                status = series.DownloadedEpisodes > 0 ? "Downloaded" : "In library";
        }
        else if (libraryMovies.TryGetValue(result.Id, out TvMovie? movie))
        {
            status = movie.HasFile ? "Downloaded" : "In library";
        }

        return new AssistantMediaSummary(
            SourceKey,
            result.IsTv ? $"{SourceKey}:series:{result.Id}" : $"{SourceKey}:movie:{result.Id}",
            result.IsTv ? AssistantMediaKind.Series : AssistantMediaKind.Movie,
            result.DisplayTitle,
            result.IsTv ? "Series" : "Movie",
            result.Year > 0 ? result.Year : null,
            status,
            TmdbService.GetPosterUrl(result.PosterPath));
    }

    static void AddCastFacts(List<string> facts, TmdbCredits? credits)
    {
        if (credits is null)
            return;

        if (credits.Cast.Count > 0)
        {
            facts.Add("Cast: " + string.Join(", ",
                credits.Cast.OrderBy(c => c.Order).Take(6).Select(c => c.Name)));
        }

        List<string> directors = credits.Crew
            .Where(c => string.Equals(c.Job, "Director", StringComparison.OrdinalIgnoreCase))
            .Select(c => c.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToList();
        if (directors.Count > 0)
            facts.Add("Director: " + string.Join(", ", directors));
    }

    static void AddIf(List<string> facts, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            facts.Add($"{label}: {value}");
    }

    static string? FormatSize(long bytes) => bytes switch
    {
        <= 0 => null,
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0} MB",
        _ => $"{bytes / 1024.0:0} KB"
    };

    static string DescribeWatchState(WatchState state) => state.IsPlayed
        ? $"watched{(state.PlayCount > 1 ? $" {state.PlayCount} times" : "")}"
        : state.IsInProgress
            ? $"in progress ({(int)Math.Round(state.PositionFraction * 100)}%)"
            : "not watched";

    static int? ParseYear(string? date) =>
        DateTime.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsed)
            ? parsed.Year
            : null;

    static bool WantsTv(string? domain) =>
        string.IsNullOrWhiteSpace(domain) || WantsMovies(domain) || WantsShows(domain);

    static bool WantsMovies(string? domain) =>
        string.IsNullOrWhiteSpace(domain)
        || domain.Contains("movie", StringComparison.OrdinalIgnoreCase)
        || domain.Contains("film", StringComparison.OrdinalIgnoreCase)
        || domain.Contains("anime", StringComparison.OrdinalIgnoreCase);

    static bool WantsShows(string? domain) =>
        string.IsNullOrWhiteSpace(domain)
        || domain.Contains("series", StringComparison.OrdinalIgnoreCase)
        || domain.Contains("tv", StringComparison.OrdinalIgnoreCase)
        || domain.Contains("show", StringComparison.OrdinalIgnoreCase)
        || domain.Contains("episode", StringComparison.OrdinalIgnoreCase)
        || domain.Contains("anime", StringComparison.OrdinalIgnoreCase)
        || domain.Contains("animation", StringComparison.OrdinalIgnoreCase);

    async Task<T> SafeAsync<T>(Func<Task<T>> action, T? fallback = default)
    {
        try
        {
            return await action();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Assistant media lookup failed");
            return fallback!;
        }
    }
}
