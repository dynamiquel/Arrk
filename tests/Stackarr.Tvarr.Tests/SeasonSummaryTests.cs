using Stackarr.Tvarr.Components.Shared;
using Stackarr.Ui.Components;

namespace Stackarr.Tvarr.Tests;

/// <summary>
/// Covers the season-selector view-model helpers used by the series dialog: the tab progress
/// badge (count + colour) and the default season choice.
/// </summary>
public class SeasonSummaryTests
{
    // Progress badge

    [Test]
    public async Task Progress_ReturnsNulls_WhenSeasonHasNoKnownEpisodes()
    {
        (string? count, string? variant) = SeasonSummary.Progress(0, 0);

        await Assert.That(count).IsNull();
        await Assert.That(variant).IsNull();
    }

    [Test]
    public async Task Progress_Neutral_WhenNothingDownloaded()
    {
        (string? count, string? variant) = SeasonSummary.Progress(0, 10);

        await Assert.That(count).IsEqualTo("0/10");
        await Assert.That(variant).IsEqualTo("neutral");
    }

    [Test]
    public async Task Progress_Warning_WhenPartiallyDownloaded()
    {
        (string? count, string? variant) = SeasonSummary.Progress(3, 10);

        await Assert.That(count).IsEqualTo("3/10");
        await Assert.That(variant).IsEqualTo("warning");
    }

    [Test]
    public async Task Progress_Success_WhenFullyDownloaded()
    {
        (string? count, string? variant) = SeasonSummary.Progress(10, 10);

        await Assert.That(count).IsEqualTo("10/10");
        await Assert.That(variant).IsEqualTo("success");
    }

    [Test]
    public async Task Progress_Success_WhenOverDownloaded()
    {
        // Defensive: Sonarr can momentarily report more downloaded episodes than it lists.
        (string? count, string? variant) = SeasonSummary.Progress(11, 10);

        await Assert.That(count).IsEqualTo("11/10");
        await Assert.That(variant).IsEqualTo("success");
    }

    // Default season

    [Test]
    public async Task DefaultSeason_Null_WhenNoSeasons()
    {
        int? season = SeasonSummary.DefaultSeason([], []);

        await Assert.That(season).IsNull();
    }

    [Test]
    public async Task DefaultSeason_IgnoresSpecials()
    {
        int? season = SeasonSummary.DefaultSeason([0], [0]);

        await Assert.That(season).IsNull();
    }

    [Test]
    public async Task DefaultSeason_PicksLowestSeason_WhenNoneLoaded()
    {
        int? season = SeasonSummary.DefaultSeason([4, 2, 6], []);

        await Assert.That(season).IsEqualTo(2);
    }

    [Test]
    public async Task DefaultSeason_PrefersFirstSeasonWithLoadedEpisodes()
    {
        int? season = SeasonSummary.DefaultSeason([1, 2, 3, 4], [3, 4]);

        await Assert.That(season).IsEqualTo(3);
    }

    [Test]
    public async Task DefaultSeason_FallsBackToFirst_WhenOnlySpecialsLoaded()
    {
        int? season = SeasonSummary.DefaultSeason([1, 2], [0]);

        await Assert.That(season).IsEqualTo(1);
    }
}
