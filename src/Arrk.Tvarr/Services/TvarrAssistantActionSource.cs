using System.Globalization;
using Microsoft.Extensions.Logging;
using Arrk.Ai;
using Arrk.Core.Enums;
using Arrk.Core.Models;
using Arrk.Core.Playback;
using Arrk.Core.Requests;
using Arrk.Jellyfin;
using Arrk.Requests;

namespace Arrk.Tvarr.Services;

/// <summary>
/// The TV/movie action surface for the assistant. Ids are the same <c>tv:…</c> ids the media source
/// returns; every action calls the same service the Movies/Shows UI uses, so permissions, request
/// quotas, notifications and lifecycle events behave identically.
/// </summary>
public sealed class TvarrAssistantActionSource(
    MediaCatalogService catalog,
    RadarrService radarr,
    SonarrService sonarr,
    TmdbService tmdb,
    WatchlistService watchlists,
    ITvPlayerLinks jellyfin,
    WatchStateService watchState,
    ILogger<TvarrAssistantActionSource> logger) : IAssistantMediaActionSource
{
    public string Key => TvarrAssistantMediaSource.SourceKey;

    public bool IsAvailable => true;

    public IReadOnlyList<AssistantActionKind> SupportedActions =>
    [
        AssistantActionKind.Request,
        AssistantActionKind.AddToWatchlist,
        AssistantActionKind.RemoveFromWatchlist,
        AssistantActionKind.Delete,
        AssistantActionKind.Upgrade,
        AssistantActionKind.Play
    ];

    public async Task<AssistantActionOutcome> ExecuteAsync(
        AssistantActionKind action,
        string id,
        string? option,
        bool confirmed,
        AssistantToolContext context,
        CancellationToken ct)
    {
        if (Parse(id) is not ParsedTvId parsed)
            return AssistantActionOutcome.Fail("I could not read that TV/movie id.");

        return action switch
        {
            AssistantActionKind.Request => await RequestAsync(parsed, option, context, ct),
            AssistantActionKind.AddToWatchlist => await WatchlistAsync(parsed, option, add: true, context, ct),
            AssistantActionKind.RemoveFromWatchlist => await WatchlistAsync(parsed, option, add: false, context, ct),
            AssistantActionKind.Delete => await DeleteAsync(parsed, confirmed, context, ct),
            AssistantActionKind.Upgrade => await UpgradeAsync(parsed, option, context, ct),
            AssistantActionKind.Play => await PlayAsync(parsed, context, ct),
            _ => AssistantActionOutcome.Fail($"Movies & TV cannot {action.ToString().ToLowerInvariant()}.")
        };
    }

    //  Request

    async Task<AssistantActionOutcome> RequestAsync(
        ParsedTvId parsed, string? quality, AssistantToolContext context, CancellationToken ct)
    {
        RequestService? requests = context.Services.GetService(typeof(RequestService)) as RequestService;
        if (requests is null || !requests.Enabled)
            return AssistantActionOutcome.Fail("Requests are turned off for this Arrk instance.");

        RequestTarget target = await BuildRequestTargetAsync(parsed, ct);
        RequestOperationResult result = await requests.CreateAsync(
            context.UserId, context.Username, target,
            string.IsNullOrWhiteSpace(quality) ? null : quality.Trim(), ct);

        if (!result.Success)
            return AssistantActionOutcome.Fail(result.Message ?? "The request could not be created.");

        string status = result.Request?.Status.ToString() ?? "Pending";
        string message = status switch
        {
            "Approved" => $"Requested {target.Title} and it was approved automatically.",
            "Failed" => $"Requested {target.Title}, but fulfilment failed: {result.Request?.DecisionReason ?? "check Requests"}.",
            _ => $"Requested {target.Title}. It is waiting for approval."
        };
        return AssistantActionOutcome.Ok(message);
    }

    async Task<RequestTarget> BuildRequestTargetAsync(ParsedTvId parsed, CancellationToken ct)
    {
        string title = "";
        int? year = null;
        int tmdbId = parsed.TmdbId;
        int tvdbId = 0;

        if (parsed.Kind == AssistantMediaKind.Movie)
        {
            TvMovie? movie = await catalog.GetLibraryMovieAsync(tmdbId, ct);
            if (movie is not null)
            {
                title = movie.Title;
                year = movie.Year > 0 ? movie.Year : null;
            }
            else
            {
                TmdbMovieDetail? detail = await tmdb.GetMovieDetailAsync(tmdbId, ct);
                title = detail?.Title ?? $"TMDb {tmdbId}";
                year = detail?.Year > 0 ? detail.Year : null;
            }
        }
        else
        {
            TvSeries? series = await catalog.GetLibrarySeriesAsync(tmdbId, ct);
            tvdbId = series?.TvdbId ?? await catalog.ResolveTvdbIdAsync(tmdbId, ct);
            if (series is not null)
            {
                title = series.Title;
                year = series.Year > 0 ? series.Year : null;
            }
            else
            {
                TmdbTvDetail? detail = await tmdb.GetTvDetailAsync(tmdbId, ct);
                title = detail?.Name ?? $"TMDb {tmdbId}";
                year = detail?.Year > 0 ? detail.Year : null;
            }
        }

        Dictionary<string, string> ids = ProviderIdMap.Build(
            (TvProviderIds.Tmdb, tmdbId.ToString(CultureInfo.InvariantCulture)),
            (TvProviderIds.Tvdb, tvdbId > 0 ? tvdbId.ToString(CultureInfo.InvariantCulture) : null));

        return parsed.Kind switch
        {
            AssistantMediaKind.Movie => new RequestTarget(
                TvMediaDomains.Movie, RequestKind.Movie, ids, title, year),
            AssistantMediaKind.Season => new RequestTarget(
                TvMediaDomains.Tv, RequestKind.Season, ids, title, year, parsed.SeasonNumber),
            AssistantMediaKind.Episode => new RequestTarget(
                TvMediaDomains.Tv, RequestKind.Episode, ids, title, year, parsed.SeasonNumber, parsed.EpisodeNumber),
            _ => new RequestTarget(TvMediaDomains.Tv, RequestKind.Series, ids, title, year)
        };
    }

    //  Watchlist

    async Task<AssistantActionOutcome> WatchlistAsync(
        ParsedTvId parsed, string? listName, bool add, AssistantToolContext context, CancellationToken ct)
    {
        TvSeries? series = null;
        int tvdbId = 0;
        string title;
        int year;
        string? poster;
        bool isSeries = parsed.Kind != AssistantMediaKind.Movie;
        int tmdbId = parsed.TmdbId;

        if (isSeries)
        {
            series = await catalog.GetLibrarySeriesAsync(tmdbId, ct);
            tvdbId = series?.TvdbId ?? await catalog.ResolveTvdbIdAsync(tmdbId, ct);
            if (series is not null)
            {
                title = series.Title;
                year = series.Year;
                poster = series.PosterUrl;
            }
            else
            {
                TmdbTvDetail? detail = await tmdb.GetTvDetailAsync(tmdbId, ct);
                if (detail is null)
                    return AssistantActionOutcome.Fail("I could not load that series.");
                title = detail.Name;
                year = detail.Year;
                poster = TmdbService.GetPosterUrl(detail.PosterPath);
            }
        }
        else
        {
            TvMovie? movie = await catalog.GetLibraryMovieAsync(tmdbId, ct);
            if (movie is not null)
            {
                title = movie.Title;
                year = movie.Year;
                poster = movie.PosterUrl;
            }
            else
            {
                TmdbMovieDetail? detail = await tmdb.GetMovieDetailAsync(tmdbId, ct);
                if (detail is null)
                    return AssistantActionOutcome.Fail("I could not load that movie.");
                title = detail.Title;
                year = detail.Year;
                poster = TmdbService.GetPosterUrl(detail.PosterPath);
            }
        }

        string itemKey = isSeries
            ? MediaItemKeys.Series(tvdbId, tmdbId)
            : MediaItemKeys.Movie(tmdbId);

        if (!add)
        {
            IReadOnlyList<int> containing = await watchlists.GetContainingAsync(
                context.UserId, WatchActivityServices.Tv, itemKey, ct);
            if (containing.Count == 0)
                return AssistantActionOutcome.Fail($"{title} is not on a watchlist.");

            int removed = 0;
            foreach (int listId in containing)
            {
                if (await watchlists.RemoveItemAsync(context.UserId, listId, itemKey, ct))
                    removed++;
            }

            return removed > 0
                ? AssistantActionOutcome.Ok($"Removed {title} from the watchlist.")
                : AssistantActionOutcome.Fail($"I could not remove {title} from the watchlist.");
        }

        int targetList = await ResolveListAsync(context.UserId, listName, ct);
        if (targetList == 0)
            return AssistantActionOutcome.Fail("I could not create a watchlist.");

        WatchlistItemRef item = new(
            WatchActivityServices.Tv,
            isSeries ? TvMediaDomains.Tv : TvMediaDomains.Movie,
            itemKey,
            title,
            isSeries ? "Series" : "Movie",
            string.IsNullOrWhiteSpace(poster) ? null : poster,
            year > 0 ? year : null,
            tmdbId,
            tvdbId);

        bool ok = await watchlists.AddItemAsync(context.UserId, targetList, item, ct);
        return ok
            ? AssistantActionOutcome.Ok($"Added {title} to your watchlist.")
            : AssistantActionOutcome.Fail($"I could not add {title} to the watchlist.");
    }

    async Task<int> ResolveListAsync(int userId, string? name, CancellationToken ct)
    {
        IReadOnlyList<WatchlistSummary> lists = await watchlists.GetForUserAsync(userId, ct);
        if (!string.IsNullOrWhiteSpace(name))
        {
            WatchlistSummary? named = lists.FirstOrDefault(l =>
                string.Equals(l.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
            if (named is not null)
                return named.Id;
        }

        if (lists.Count > 0)
            return lists[0].Id;

        string listName = string.IsNullOrWhiteSpace(name) ? "Watchlist" : name.Trim();
        WatchlistDetail? created = await watchlists.CreateAsync(userId, listName, null, ct);
        return created?.Id ?? 0;
    }

    //  Delete

    async Task<AssistantActionOutcome> DeleteAsync(
        ParsedTvId parsed, bool confirmed, AssistantToolContext context, CancellationToken ct)
    {
        if (!context.IsAdmin)
            return AssistantActionOutcome.Fail("Only an administrator can delete media.");

        string title = await ResolveTitleAsync(parsed, ct);

        if (parsed.Kind is AssistantMediaKind.Movie or AssistantMediaKind.Series)
        {
            if (!confirmed)
                return AssistantActionOutcome.Confirm(
                    $"Delete {title} and its files from the library? Ask the user to confirm.",
                    $"Delete {title}");

            if (parsed.Kind == AssistantMediaKind.Movie)
            {
                TvMovie? movie = await catalog.GetLibraryMovieAsync(parsed.TmdbId, ct);
                if (movie is null)
                    return AssistantActionOutcome.Fail($"{title} is not in the movie library.");

                await radarr.DeleteMovie(movie.Id, deleteFiles: true, ct);
                catalog.InvalidateLibraryCache();
                return AssistantActionOutcome.Ok($"Deleted {movie.Title} and its files.");
            }

            TvSeries? series = await catalog.GetLibrarySeriesAsync(parsed.TmdbId, ct);
            if (series is null)
                return AssistantActionOutcome.Fail($"{title} is not in the TV library.");

            await sonarr.DeleteSeries(series.Id, deleteFiles: true, ct);
            catalog.InvalidateLibraryCache();
            return AssistantActionOutcome.Ok($"Deleted {series.Title} and its files.");
        }

        // A season is deleted file-by-file; the series row (and its monitoring) stays.
        TvSeries? show = await catalog.GetLibrarySeriesAsync(parsed.TmdbId, ct);
        if (show is null)
            return AssistantActionOutcome.Fail($"{title} is not in the TV library.");
        if (parsed.SeasonNumber is null)
            return AssistantActionOutcome.Fail("Tell me which season to delete.");

        if (!confirmed)
            return AssistantActionOutcome.Confirm(
                $"Delete season {parsed.SeasonNumber} of {show.Title} and its files? Ask the user to confirm.",
                $"Delete S{parsed.SeasonNumber} of {show.Title}");

        int season = parsed.SeasonNumber.Value;
        List<TvEpisode> episodes = await sonarr.GetEpisodes(show.Id, ct);
        List<TvEpisode> withFiles = episodes
            .Where(e => e.SeasonNumber == season && e.HasFile && e.EpisodeFileId > 0)
            .ToList();

        foreach (TvEpisode episode in withFiles)
            await sonarr.DeleteEpisode(episode.EpisodeFileId, ct);

        catalog.InvalidateLibraryCache();
        return AssistantActionOutcome.Ok(
            withFiles.Count == 0
                ? $"Season {season} of {show.Title} had no files to delete."
                : $"Deleted {withFiles.Count} files from season {season} of {show.Title}.");
    }

    //  Upgrade

    async Task<AssistantActionOutcome> UpgradeAsync(
        ParsedTvId parsed, string? target, AssistantToolContext context, CancellationToken ct)
    {
        if (!context.IsAdmin)
            return AssistantActionOutcome.Fail("Only an administrator can change quality profiles.");

        bool uhd = string.IsNullOrWhiteSpace(target)
            || target.Contains("4k", StringComparison.OrdinalIgnoreCase)
            || target.Contains("uhd", StringComparison.OrdinalIgnoreCase)
            || target.Contains("2160", StringComparison.OrdinalIgnoreCase);

        string profileName = uhd ? "UHD" : target!.Trim();
        string title = await ResolveTitleAsync(parsed, ct);

        if (parsed.Kind == AssistantMediaKind.Movie)
        {
            TvMovie? movie = await catalog.GetLibraryMovieAsync(parsed.TmdbId, ct);
            if (movie is null)
                return AssistantActionOutcome.Fail($"{title} is not in the movie library.");
            if (uhd && int.TryParse(movie.Quality.Resolution, out int resolution) && resolution >= 2160)
                return AssistantActionOutcome.Ok($"{movie.Title} is already 4K.");

            int? profileId = await radarr.GetQualityProfileId(profileName, ct);
            if (profileId is null)
                return AssistantActionOutcome.Fail($"No quality profile named {profileName} exists.");

            await radarr.SetQualityProfile(movie.Id, profileId.Value, ct);
            await radarr.CommandSearchMovie(movie.Id, ct);
            catalog.InvalidateLibraryCache();
            return AssistantActionOutcome.Ok(
                $"Switched {movie.Title} to {profileName} and started a search for an upgrade.");
        }

        TvSeries? series = await catalog.GetLibrarySeriesAsync(parsed.TmdbId, ct);
        if (series is null)
            return AssistantActionOutcome.Fail($"{title} is not in the TV library.");

        int? seriesProfile = await sonarr.GetQualityProfileId(profileName, ct);
        if (seriesProfile is null)
            return AssistantActionOutcome.Fail($"No quality profile named {profileName} exists.");

        await sonarr.UpdateSeriesMonitoring(series.TvdbId, seriesProfile.Value, null, true, ct);
        await sonarr.CommandSearchSeries(series.Id, ct);
        catalog.InvalidateLibraryCache();
        return AssistantActionOutcome.Ok(
            $"Switched {series.Title} to {profileName} and started a search for an upgrade.");
    }

    //  Play

    async Task<AssistantActionOutcome> PlayAsync(
        ParsedTvId parsed, AssistantToolContext context, CancellationToken ct)
    {
        if (!jellyfin.IsAvailable)
            return AssistantActionOutcome.Fail("Jellyfin is not enabled, so there is no player to start.");

        if (parsed.Kind == AssistantMediaKind.Movie)
        {
            TvMovie? movie = await catalog.GetLibraryMovieAsync(parsed.TmdbId, ct);
            if (movie is null)
                return AssistantActionOutcome.Fail("That movie is not in the library.");
            if (!movie.HasFile)
                return AssistantActionOutcome.Fail($"{movie.Title} is not downloaded yet.");

            string? url = PlayTarget(await jellyfin.GetMovieUrlAsync(movie, ct));
            return url is null
                ? AssistantActionOutcome.Fail($"I could not find {movie.Title} in Jellyfin.")
                : AssistantActionOutcome.Ok($"Ready to play {movie.Title}.",
                    new AssistantActionLink(AssistantActionLinkKind.Open, $"Play {movie.Title}", url));
        }

        TvSeries? series = await catalog.GetLibrarySeriesAsync(parsed.TmdbId, ct);
        if (series is null)
            return AssistantActionOutcome.Fail("That series is not in the library.");

        TvSeriesLinks? links = await jellyfin.GetSeriesLinksAsync(series, ct);
        if (links is null)
            return AssistantActionOutcome.Fail($"I could not find {series.Title} in Jellyfin.");

        if (parsed.Kind == AssistantMediaKind.Episode && parsed.SeasonNumber is not null)
        {
            string? episodeUrl = jellyfin.GetEpisodeUrl(
                links, parsed.SeasonNumber.Value, parsed.EpisodeNumber ?? 0);
            string? episodeTarget = PlayTarget(episodeUrl);
            return episodeTarget is null
                ? AssistantActionOutcome.Fail("That episode is not available in Jellyfin.")
                : AssistantActionOutcome.Ok(
                    $"Ready to play S{parsed.SeasonNumber:00}E{parsed.EpisodeNumber:00} of {series.Title}.",
                    new AssistantActionLink(AssistantActionLinkKind.Open,
                        $"Play {series.Title} S{parsed.SeasonNumber:00}E{parsed.EpisodeNumber:00}",
                        episodeTarget));
        }

        // A whole series resumes the next episode after the last watched one, then falls back to
        // the first available episode, and only then to the series page.
        (int Season, int Episode) next = NextEpisode(links);
        if (!string.IsNullOrWhiteSpace(context.Username) && watchState.IsAvailable)
        {
            try
            {
                IReadOnlyDictionary<int, SeriesWatchState> states = await watchState.GetSeriesStatesAsync(
                    context.Username, [parsed.TmdbId, series.TvdbId], ct);
                SeriesWatchState? state = states.GetValueOrDefault(parsed.TmdbId)
                    ?? states.GetValueOrDefault(series.TvdbId);
                if (state?.LastWatchedSeason is > 0)
                {
                    next = (state.LastWatchedSeason.Value, (state.LastWatchedEpisode ?? 0) + 1);
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not read resume point for {Title}", series.Title);
            }
        }

        string? resumeUrl = jellyfin.GetEpisodeUrl(links, next.Season, next.Episode);
        string? seriesTarget = PlayTarget(resumeUrl) ?? PlayTarget(links.SeriesUrl);
        return seriesTarget is null
            ? AssistantActionOutcome.Fail($"I could not find {series.Title} in Jellyfin.")
            : AssistantActionOutcome.Ok($"Ready to play {series.Title}.",
                new AssistantActionLink(AssistantActionLinkKind.Open, $"Play {series.Title}", seriesTarget));
    }

    static (int Season, int Episode) NextEpisode(TvSeriesLinks links)
    {
        if (links.Episodes.Count == 0)
            return (1, 1);

        return links.Episodes.Keys
            .OrderBy(key => key.Season)
            .ThenBy(key => key.Episode)
            .First();
    }

    string? PlayTarget(string? jellyfinUrl)
    {
        if (string.IsNullOrWhiteSpace(jellyfinUrl))
            return null;

        if (!jellyfin.WebPlayerEnabled)
            return jellyfinUrl;

        string? itemId = jellyfin.ExtractItemId(jellyfinUrl);
        return itemId is null ? jellyfinUrl : jellyfin.BuildWatchUrl(itemId);
    }

    //  Helpers

    async Task<string> ResolveTitleAsync(ParsedTvId parsed, CancellationToken ct)
    {
        if (parsed.Kind == AssistantMediaKind.Movie)
        {
            TvMovie? movie = await catalog.GetLibraryMovieAsync(parsed.TmdbId, ct);
            if (movie is not null)
                return movie.Title;
            TmdbMovieDetail? detail = await tmdb.GetMovieDetailAsync(parsed.TmdbId, ct);
            return detail?.Title ?? $"TMDb {parsed.TmdbId}";
        }

        TvSeries? series = await catalog.GetLibrarySeriesAsync(parsed.TmdbId, ct);
        if (series is not null)
            return series.Title;
        TmdbTvDetail? show = await tmdb.GetTvDetailAsync(parsed.TmdbId, ct);
        return show?.Name ?? $"TMDb {parsed.TmdbId}";
    }

    static ParsedTvId? Parse(string id)
    {
        string[] parts = id.Split(':');
        if (parts.Length < 3 || !string.Equals(parts[0], TvarrAssistantMediaSource.SourceKey, StringComparison.OrdinalIgnoreCase))
            return null;
        if (!int.TryParse(parts[2], out int tmdbId) || tmdbId <= 0)
            return null;

        int? season = parts.Length >= 4 && int.TryParse(parts[3], out int s) ? s : null;
        int? episode = parts.Length >= 5 && int.TryParse(parts[4], out int e) ? e : null;

        return parts[1].ToLowerInvariant() switch
        {
            "movie" => new ParsedTvId(AssistantMediaKind.Movie, tmdbId, null, null),
            "series" => new ParsedTvId(AssistantMediaKind.Series, tmdbId, null, null),
            "season" when season is not null => new ParsedTvId(AssistantMediaKind.Season, tmdbId, season, null),
            "episode" when season is not null && episode is not null
                => new ParsedTvId(AssistantMediaKind.Episode, tmdbId, season, episode),
            _ => null
        };
    }

    readonly record struct ParsedTvId(
        AssistantMediaKind Kind, int TmdbId, int? SeasonNumber, int? EpisodeNumber);
}
