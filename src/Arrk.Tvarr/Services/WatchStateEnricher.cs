using Arrk.Tvarr.Components.Shared;
using Arrk.Core.Enums;
using Arrk.Core.Playback;

namespace Arrk.Tvarr.Services;

/// <summary>
/// Projects player watch state onto catalog items. Kept out of the Blazor components so the
/// progress/watched logic is unit-testable and independent of which player is configured.
/// </summary>
public sealed class WatchStateEnricher(WatchStateService watch)
{
    public bool IsAvailable => watch.IsAvailable;

    public async Task<List<MediaItem>> EnrichAsync(
        string username,
        IReadOnlyList<MediaItem> items,
        CancellationToken ct = default)
    {
        if (!watch.IsAvailable || items.Count == 0 || string.IsNullOrWhiteSpace(username))
            return items.ToList();

        List<MediaItem> enriched = items.ToList();

        // Movies: per-item state keyed by TMDb id.
        List<MediaKey> movieKeys = enriched
            .Where(IsMovie)
            .Where(i => i.TmdbId > 0)
            .Select(i => new MediaKey(TvMediaDomains.Movie, i.TmdbId))
            .Distinct()
            .ToList();

        // Series: aggregated watched/total keyed by TMDb or TVDB id.
        List<int> seriesIds = enriched
            .Where(IsSeries)
            .SelectMany(i => new[] { i.TmdbId, i.TvdbId })
            .Where(id => id > 0)
            .Distinct()
            .ToList();

        IReadOnlyDictionary<MediaKey, WatchState> movieStates = movieKeys.Count > 0
            ? await watch.GetStatesAsync(username, movieKeys, ct)
            : new Dictionary<MediaKey, WatchState>();

        IReadOnlyDictionary<int, SeriesWatchState> seriesStates = seriesIds.Count > 0
            ? await watch.GetSeriesStatesAsync(username, seriesIds, ct)
            : new Dictionary<int, SeriesWatchState>();

        for (int i = 0; i < enriched.Count; i++)
        {
            MediaItem item = enriched[i];

            if (IsMovie(item) && item.TmdbId > 0 &&
                movieStates.TryGetValue(new MediaKey(TvMediaDomains.Movie, item.TmdbId), out WatchState? state))
            {
                enriched[i] = item with
                {
                    IsWatched = state.IsPlayed,
                    WatchProgress = state.IsPlayed ? 1 : state.PositionFraction,
                    ResumeTicks = state.PositionTicks,
                    LastPlayed = state.LastPlayed
                };
            }
            else if (IsSeries(item))
            {
                SeriesWatchState? series =
                    (item.TvdbId > 0 && seriesStates.TryGetValue(item.TvdbId, out SeriesWatchState? byTvdb)) ? byTvdb :
                    (item.TmdbId > 0 && seriesStates.TryGetValue(item.TmdbId, out SeriesWatchState? byTmdb)) ? byTmdb : null;

                if (series is not null && series.TotalEpisodes > 0)
                {
                    enriched[i] = item with
                    {
                        IsWatched = series.IsFullyWatched,
                        WatchProgress = series.Progress
                    };
                }
            }
        }

        return enriched;
    }

    static bool IsMovie(MediaItem item) =>
        string.Equals(item.MediaType, TmdbMediaType.Movie, StringComparison.OrdinalIgnoreCase);

    static bool IsSeries(MediaItem item) =>
        string.Equals(item.MediaType, TmdbMediaType.Tv, StringComparison.OrdinalIgnoreCase) || item.TvdbId > 0;
}