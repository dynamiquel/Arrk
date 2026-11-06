namespace Arrk.Core.Playback;

/// <summary>
/// Rules that make the Continue Watching shelf behave like a streaming service. A player supplies
/// raw resume/next-up candidates; these rules decide what counts as finished and which single
/// episode represents a series.
/// </summary>
public static class ContinueWatchingRules
{
    /// <summary>Most time left that still counts as finished for a film (3 minutes).</summary>
    public static readonly TimeSpan MaxFinishedRemaining = TimeSpan.FromMinutes(3);

    /// <summary>Least time left that counts as finished for a film (1 minute).</summary>
    public static readonly TimeSpan MinFinishedRemaining = TimeSpan.FromMinutes(1);

    /// <summary>Share of a film runtime treated as "the end", clamped to the minute bounds above.</summary>
    public const double FinishedRemainingFraction = 0.08;

    /// <summary>Most time left that still counts as finished for an episode (1 minute).</summary>
    public static readonly TimeSpan EpisodeFinishedRemaining = TimeSpan.FromMinutes(1);

    /// <summary>Least time left that counts as finished for an episode (20 seconds).</summary>
    public static readonly TimeSpan MinEpisodeFinishedRemaining = TimeSpan.FromSeconds(20);

    /// <summary>Share of an episode runtime treated as "the end" (5%, clamped to 20–60 seconds).</summary>
    public const double EpisodeFinishedRemainingFraction = 0.05;

    /// <summary>
    /// True when the user has watched far enough into an item that the rest is just the tail
    /// (credits, outro). Episodes use a much smaller window than films: 5% of the runtime clamped
    /// to 20–60 seconds, so a 24-minute episode is finished at ~1 minute from the end while a
    /// two-hour film gets up to three minutes.
    /// </summary>
    public static bool IsEffectivelyFinished(long positionTicks, long runtimeTicks, bool isEpisode = false)
    {
        if (positionTicks <= 0 || runtimeTicks <= 0)
            return false;

        long remaining = Math.Max(0, runtimeTicks - positionTicks);
        long window = isEpisode ? EpisodeFinishedTicks(runtimeTicks) : FinishedRemainingTicks(runtimeTicks);
        return remaining <= window;
    }

    /// <summary>The tail window for a film runtime, in ticks.</summary>
    public static long FinishedRemainingTicks(long runtimeTicks)
    {
        if (runtimeTicks <= 0)
            return 0;

        long fraction = (long)(runtimeTicks * FinishedRemainingFraction);
        return Math.Clamp(fraction, MinFinishedRemaining.Ticks, MaxFinishedRemaining.Ticks);
    }

    /// <summary>The tail window for an episode runtime, in ticks.</summary>
    public static long EpisodeFinishedTicks(long runtimeTicks)
    {
        if (runtimeTicks <= 0)
            return 0;

        long fraction = (long)(runtimeTicks * EpisodeFinishedRemainingFraction);
        return Math.Clamp(fraction, MinEpisodeFinishedRemaining.Ticks, EpisodeFinishedRemaining.Ticks);
    }

    /// <summary>
    /// A stable identity for the series an episode belongs to. Provider ids are preferred so a
    /// missing/renamed title cannot merge two different shows; the title is only a last resort.
    /// </summary>
    public static string SeriesKey(ContinueWatchingItem item)
    {
        if (item.SeriesTmdbId > 0)
            return $"tmdb:{item.SeriesTmdbId}";
        if (item.SeriesTvdbId > 0)
            return $"tvdb:{item.SeriesTvdbId}";

        string title = item.ParentTitle ?? item.Title;
        return "series:" + title.Trim().ToLowerInvariant();
    }

    /// <summary>
    /// Picks the episode a series should represent: the most recently played one, then the
    /// furthest episode, so an older half-watched episode never hides the one the user actually
    /// moved on to.
    /// </summary>
    public static T SelectLatest<T>(IEnumerable<T> entries, Func<T, ContinueWatchingItem> selector) =>
        entries
            .OrderByDescending(entry => selector(entry).LastPlayed ?? DateTime.MinValue)
            .ThenByDescending(entry => selector(entry).Key.SeasonNumber)
            .ThenByDescending(entry => selector(entry).Key.EpisodeNumber)
            .ThenByDescending(entry => selector(entry).Progress)
            .First();

    public static ContinueWatchingItem SelectLatest(IEnumerable<ContinueWatchingItem> items) =>
        SelectLatest(items, item => item);
}
