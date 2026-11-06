using Microsoft.Extensions.Logging;
using Arrk.Core.Enums;
using Arrk.Core.Lifecycle;
using Arrk.Core.Models;
using Arrk.Core.Playback;
using Arrk.Tvarr.Services;

namespace Arrk.Tvarr.Services;

/// <summary>
/// Exposes movies and series seasons as lifecycle-managed items. Seasons are the staleness unit for
/// TV, so one unwatched season can be reclaimed without touching the rest of the show. The lifecycle
/// module only sees this contract, so TV remains an optional provider.
/// </summary>
public sealed class TvManagedMediaProvider(
    RadarrService radarr,
    SonarrService sonarr,
    WatchlistService watchlists,
    ILogger<TvManagedMediaProvider> logger) : IManagedMediaProvider
{
    public string Key => WatchActivityServices.Tv;

    public string DisplayName => "TV";

    public async Task<IReadOnlyList<ManagedMediaItem>> GetItemsAsync(CancellationToken ct = default)
    {
        IReadOnlySet<string> bookmarked = await watchlists.GetTrackedItemKeysAsync(Key, ct);
        List<ManagedMediaItem> items = [];

        List<TvMovie> movies;
        try
        {
            movies = await radarr.GetAllMovies(ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not list movies for staleness");
            movies = [];
        }

        foreach (TvMovie movie in movies)
        {
            if (!movie.HasFile || movie.SizeOnDisk <= 0 || movie.TmdbId <= 0)
                continue;

            string itemKey = MediaItemKeys.Movie(movie.TmdbId);
            bool kept = bookmarked.Contains(itemKey);
            items.Add(new ManagedMediaItem(
                Key,
                itemKey,
                TvMediaDomains.Movie,
                "movie",
                movie.Title,
                movie.Year > 0 ? movie.Year.ToString() : null,
                movie.PosterUrl,
                movie.SizeOnDisk,
                NormalizeAdded(movie.Added),
                kept,
                kept ? "Bookmarked" : null,
                movie.Id.ToString()));
        }

        List<TvSeries> series;
        try
        {
            series = await sonarr.GetAllSeries(ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not list series for staleness");
            series = [];
        }

        foreach (TvSeries show in series)
        {
            string seriesKey = MediaItemKeys.Series(show.TvdbId, show.TmdbId);
            if (string.IsNullOrEmpty(seriesKey))
                continue;

            bool kept = bookmarked.Contains(seriesKey);
            List<SeasonCandidate> seasons = show.Seasons
                .Where(s => s.SeasonNumber > 0 && s.SizeOnDisk > 0)
                .Select(s => new SeasonCandidate(s.SeasonNumber, s.SizeOnDisk, s.DownloadedEpisodeCount))
                .ToList();

            // Older Sonarr responses can omit per-season statistics. Fall back to the episode
            // files once so a show still participates in staleness.
            if (seasons.Count == 0 && show.SizeOnDisk is > 0)
            {
                try
                {
                    List<TvEpisode> episodes = await sonarr.GetEpisodes(show.Id, ct);
                    seasons = episodes
                        .Where(e => e.SeasonNumber > 0 && e.HasFile)
                        .GroupBy(e => e.SeasonNumber)
                        .Select(g => new SeasonCandidate(
                            g.Key,
                            g.Sum(e => e.File?.SizeBytes ?? 0),
                            g.Count()))
                        .Where(s => s.SizeOnDisk > 0)
                        .ToList();
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Could not read episodes for series {Title} staleness", show.Title);
                }
            }

            foreach (SeasonCandidate season in seasons)
            {
                items.Add(new ManagedMediaItem(
                    Key,
                    MediaItemKeys.Season(seriesKey, season.SeasonNumber),
                    TvMediaDomains.Tv,
                    "season",
                    show.Title,
                    $"Season {season.SeasonNumber} · {season.EpisodeCount} episode{(season.EpisodeCount == 1 ? "" : "s")}",
                    show.PosterUrl,
                    season.SizeOnDisk,
                    NormalizeAdded(show.Added),
                    kept,
                    kept ? "Bookmarked" : null,
                    $"{show.Id}:{season.SeasonNumber}",
                    seriesKey));
            }
        }

        return items;
    }

    public async Task<ManagedMediaDeleteResult> DeleteAsync(ManagedMediaItem item, CancellationToken ct = default)
    {
        try
        {
            if (item.Domain == TvMediaDomains.Movie)
            {
                if (!int.TryParse(item.LibraryId, out int movieId) || movieId <= 0)
                    return new ManagedMediaDeleteResult(false, 0, "The movie id is missing.");
                await radarr.DeleteMovie(movieId, deleteFiles: true, ct);
                return new ManagedMediaDeleteResult(true, item.SizeOnDisk);
            }

            string[] parts = (item.LibraryId ?? "").Split(':', 2);
            if (parts.Length != 2
                || !int.TryParse(parts[0], out int seriesId)
                || !int.TryParse(parts[1], out int seasonNumber)
                || seriesId <= 0
                || seasonNumber <= 0)
            {
                return new ManagedMediaDeleteResult(false, 0, "The season id is missing.");
            }

            List<TvEpisode> episodes = await sonarr.GetEpisodes(seriesId, ct);
            long freed = 0;
            foreach (TvEpisode episode in episodes.Where(e => e.SeasonNumber == seasonNumber && e.HasFile))
            {
                if (episode.EpisodeFileId <= 0)
                    continue;
                freed += episode.File?.SizeBytes ?? 0;
                await sonarr.DeleteEpisode(episode.EpisodeFileId, ct);
            }

            // Keep the reclaimed season from being re-downloaded by the arr service.
            await sonarr.ToggleSeasonMonitored(seriesId, seasonNumber, monitored: false, ct);
            return new ManagedMediaDeleteResult(true, freed > 0 ? freed : item.SizeOnDisk);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not delete stale {Type} {Title}", item.ItemType, item.Title);
            return new ManagedMediaDeleteResult(false, 0, ex.Message);
        }
    }

    static DateTime NormalizeAdded(DateTime added) =>
        added == default ? DateTime.UtcNow.AddDays(-1) : added.ToUniversalTime();

    sealed record SeasonCandidate(int SeasonNumber, long SizeOnDisk, int EpisodeCount);
}
