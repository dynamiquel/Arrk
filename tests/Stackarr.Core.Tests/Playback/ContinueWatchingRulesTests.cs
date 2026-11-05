using Stackarr.Core.Enums;
using Stackarr.Core.Playback;

namespace Stackarr.Core.Tests.Playback;

/// <summary>
/// Defines what "finished" means for the Continue Watching shelf. Titles inside the tail window are
/// treated as finished, so they disappear (films) or turn into the next episode (series) instead
/// of inviting the user back to watch the credits.
/// </summary>
public class ContinueWatchingRulesTests
{
    static readonly long Minute = TimeSpan.FromMinutes(1).Ticks;

    [Test]
    public async Task UnstartedItem_IsNotFinished()
    {
        await Assert.That(ContinueWatchingRules.IsEffectivelyFinished(0, 24 * Minute)).IsFalse();
        await Assert.That(ContinueWatchingRules.IsEffectivelyFinished(0, 0)).IsFalse();
    }

    [Test]
    public async Task EpisodeNearTheEnd_UsesTheShorterEpisodeWindow()
    {
        long runtime = 24 * Minute;

        await Assert.That(ContinueWatchingRules.IsEffectivelyFinished(
            runtime - 45 * TimeSpan.TicksPerSecond, runtime, isEpisode: true)).IsTrue();
        await Assert.That(ContinueWatchingRules.IsEffectivelyFinished(
            runtime - 2 * Minute, runtime, isEpisode: true)).IsFalse();
    }

    [Test]
    public async Task ShortEpisode_UsesTheEpisodeFloor()
    {
        long runtime = 8 * Minute;

        await Assert.That(ContinueWatchingRules.EpisodeFinishedTicks(runtime))
            .IsEqualTo(24 * TimeSpan.TicksPerSecond);
        await Assert.That(ContinueWatchingRules.EpisodeFinishedTicks(24 * Minute))
            .IsEqualTo(TimeSpan.FromMinutes(1).Ticks);
    }

    [Test]
    public async Task LongMovie_UsesTheThreeMinuteCeiling()
    {
        long runtime = 120 * Minute;

        await Assert.That(ContinueWatchingRules.IsEffectivelyFinished(runtime - 2 * Minute, runtime)).IsTrue();
        await Assert.That(ContinueWatchingRules.IsEffectivelyFinished(runtime - 4 * Minute, runtime)).IsFalse();
    }

    [Test]
    public async Task VeryShortItem_UsesTheOneMinuteFloor()
    {
        long runtime = 6 * Minute;

        await Assert.That(ContinueWatchingRules.IsEffectivelyFinished(runtime - 50 * TimeSpan.TicksPerSecond, runtime)).IsTrue();
        await Assert.That(ContinueWatchingRules.IsEffectivelyFinished(runtime - 90 * TimeSpan.TicksPerSecond, runtime)).IsFalse();
    }

    [Test]
    public async Task TailWindow_IsEightPercentClampedToOneToThreeMinutes()
    {
        await Assert.That(ContinueWatchingRules.FinishedRemainingTicks(6 * Minute)).IsEqualTo(Minute);
        await Assert.That(ContinueWatchingRules.FinishedRemainingTicks(24 * Minute))
            .IsEqualTo((long)(24 * Minute * 0.08));
        await Assert.That(ContinueWatchingRules.FinishedRemainingTicks(120 * Minute)).IsEqualTo(3 * Minute);
    }

    [Test]
    public async Task SelectLatest_PrefersTheMostRecentlyPlayedEpisode()
    {
        DateTime now = DateTime.UtcNow;
        ContinueWatchingItem older = Episode(1, 1, now.AddHours(-2));
        ContinueWatchingItem newer = Episode(1, 2, now.AddHours(-1));

        await Assert.That(ContinueWatchingRules.SelectLatest([older, newer])).IsEqualTo(newer);
        await Assert.That(ContinueWatchingRules.SelectLatest([newer, older])).IsEqualTo(newer);
    }

    [Test]
    public async Task SelectLatest_WithoutDatesPrefersTheFurthestEpisode()
    {
        ContinueWatchingItem first = Episode(1, 1, null);
        ContinueWatchingItem second = Episode(1, 2, null);
        ContinueWatchingItem nextSeason = Episode(2, 1, null);

        await Assert.That(ContinueWatchingRules.SelectLatest([first, second])).IsEqualTo(second);
        await Assert.That(ContinueWatchingRules.SelectLatest([second, nextSeason])).IsEqualTo(nextSeason);
    }

    [Test]
    public async Task SelectLatest_KeepsTheSourceEntry()
    {
        (string Id, ContinueWatchingItem Item) first = ("a", Episode(1, 1, null));
        (string Id, ContinueWatchingItem Item) second = ("b", Episode(1, 2, null));

        (string Id, ContinueWatchingItem Item) latest =
            ContinueWatchingRules.SelectLatest([first, second], entry => entry.Item);

        await Assert.That(latest.Id).IsEqualTo("b");
    }

    [Test]
    public async Task SeriesKey_PrefersProviderIdsThenTitle()
    {
        ContinueWatchingItem byTmdb = Episode(1, 1, null) with { SeriesTmdbId = 123 };
        ContinueWatchingItem byTvdb = Episode(1, 1, null) with { SeriesTvdbId = 456 };
        ContinueWatchingItem byTitle = Episode(1, 1, null) with { ParentTitle = "Re:ZERO" };

        await Assert.That(ContinueWatchingRules.SeriesKey(byTmdb)).IsEqualTo("tmdb:123");
        await Assert.That(ContinueWatchingRules.SeriesKey(byTvdb)).IsEqualTo("tvdb:456");
        await Assert.That(ContinueWatchingRules.SeriesKey(byTitle)).IsEqualTo("series:re:zero");
    }

    static ContinueWatchingItem Episode(int season, int episode, DateTime? lastPlayed) =>
        new(
            new MediaKey(new MediaDomain("tv"), SeasonNumber: season, EpisodeNumber: episode),
            $"Episode {episode}",
            null,
            null,
            null,
            null,
            "Some Show",
            0.5,
            0,
            24 * Minute,
            lastPlayed);
}
