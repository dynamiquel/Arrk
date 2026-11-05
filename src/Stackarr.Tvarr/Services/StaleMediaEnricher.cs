using Stackarr.Core.Lifecycle;
using Stackarr.Core.Models;
using Stackarr.Core.Playback;
using Stackarr.Tvarr.Components.Shared;

namespace Stackarr.Tvarr.Services;

/// <summary>
/// Projects lifecycle status onto TV cards/dialogs. Movies are evaluated directly; a series card
/// aggregates its seasons, so one stale season makes the show read as stale while a fully pinned
/// show stays fresh. It reads Core's facade only, so TV never depends on the lifecycle module.
/// </summary>
public sealed class StaleMediaEnricher(StaleMediaService stale)
{
    public bool IsAvailable => stale.IsAvailable;

    public Task<IReadOnlyDictionary<string, StaleMediaItem>> LoadAsync(CancellationToken ct = default) =>
        stale.GetStatusesAsync(WatchActivityServices.Tv, ct);

    public List<MediaItem> Enrich(IReadOnlyList<MediaItem> items, IReadOnlyDictionary<string, StaleMediaItem> statuses)
    {
        Dictionary<string, MediaStaleState> aggregated = Aggregate(statuses);
        List<MediaItem> result = new(items.Count);
        foreach (MediaItem item in items)
        {
            string key = KeyFor(item);
            if (!string.IsNullOrEmpty(key) && aggregated.TryGetValue(key, out MediaStaleState? state))
            {
                result.Add(item with
                {
                    Staleness = state.Level,
                    DaysRemaining = state.DaysRemaining,
                    IsBookmarked = state.IsBookmarked,
                    BookmarkLabel = state.BookmarkLabel,
                    IsPinned = state.IsPinned
                });
            }
            else
            {
                result.Add(item);
            }
        }
        return result;
    }

    public async Task<MediaStaleState?> GetMovieStatusAsync(int tmdbId, CancellationToken ct = default)
    {
        if (tmdbId <= 0)
            return null;
        IReadOnlyDictionary<string, StaleMediaItem> statuses = await LoadAsync(ct);
        return statuses.TryGetValue(MediaItemKeys.Movie(tmdbId), out StaleMediaItem? item)
            ? MediaStaleState.From(item)
            : null;
    }

    public async Task<MediaStaleState?> GetSeriesStatusAsync(int tvdbId, int tmdbId, CancellationToken ct = default)
    {
        string key = MediaItemKeys.Series(tvdbId, tmdbId);
        if (string.IsNullOrEmpty(key))
            return null;
        IReadOnlyDictionary<string, StaleMediaItem> statuses = await LoadAsync(ct);
        return Aggregate(statuses).TryGetValue(key, out MediaStaleState? state) ? state : null;
    }

    /// <summary>Collapses season statuses onto their parent series key.</summary>
    static Dictionary<string, MediaStaleState> Aggregate(IReadOnlyDictionary<string, StaleMediaItem> statuses)
    {
        Dictionary<string, List<StaleMediaItem>> groups = new(StringComparer.Ordinal);
        foreach (StaleMediaItem status in statuses.Values)
        {
            string key = string.IsNullOrEmpty(status.ParentItemKey) ? status.ItemKey : status.ParentItemKey;
            if (!groups.TryGetValue(key, out List<StaleMediaItem>? list))
                groups[key] = list = [];
            list.Add(status);
        }

        Dictionary<string, MediaStaleState> result = new(StringComparer.Ordinal);
        foreach ((string key, List<StaleMediaItem> items) in groups)
        {
            List<StaleMediaItem> active = items.Where(x => !x.IsPinned).ToList();
            if (active.Count == 0)
            {
                result[key] = new MediaStaleState(StaleLevel.Fresh, null, false, null, true);
                continue;
            }

            StaleLevel level = active.Max(x => x.Level);
            List<StaleMediaItem> atLevel = active.Where(x => x.Level == level).ToList();
            double? remaining = atLevel.Min(x => x.DaysRemaining ?? double.MaxValue);
            if (remaining == double.MaxValue)
                remaining = null;

            StaleMediaItem first = atLevel[0];
            result[key] = new MediaStaleState(level, remaining, first.IsBookmarked, first.BookmarkLabel, false);
        }
        return result;
    }

    public static string KeyFor(MediaItem item) => item.MediaType switch
    {
        "movie" => MediaItemKeys.Movie(item.TmdbId),
        "tv" => MediaItemKeys.Series(item.TvdbId, item.TmdbId),
        _ => ""
    };

    public static string KeyFor(TvMovie movie) =>
        movie.TmdbId > 0 ? MediaItemKeys.Movie(movie.TmdbId) : "";

    public static string KeyFor(TvSeries series) =>
        MediaItemKeys.Series(series.TvdbId, series.TmdbId);
}
