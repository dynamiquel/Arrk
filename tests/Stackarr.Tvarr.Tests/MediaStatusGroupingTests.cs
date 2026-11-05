using Stackarr.Tvarr.Components.Shared;
using Stackarr.Ui.Components;

namespace Stackarr.Tvarr.Tests;

/// <summary>
/// Covers the primary-status grouping that drives the collapsible sections on the Movies and TV Shows
/// pages: each item lands in exactly one group, groups keep a canonical order, empty groups are
/// omitted, and search hits are ordered by relevance.
/// </summary>
public class MediaStatusGroupingTests
{
    static MediaItem Movie(bool hasFile, bool downloading = false) => new()
    {
        Title = hasFile ? "Downloaded" : "Missing",
        HasFile = hasFile,
        IsDownloading = downloading,
        LibraryStatus = hasFile ? "Downloaded" : "Missing"
    };

    static MediaItem Series(string status, bool downloading = false) => new()
    {
        Title = status,
        LibraryStatus = status,
        IsDownloading = downloading,
        HasFile = status is "Downloaded" or "Downloaded Monitored" or "Partial"
    };

    [Test]
    public async Task MovieGroup_PrioritisesActiveDownloadThenMissingThenDownloaded()
    {
        await Assert.That(MediaStatusGrouping.GroupOfMovie(Movie(hasFile: false, downloading: true))).IsEqualTo(StatusGroupKind.Downloading);
        await Assert.That(MediaStatusGrouping.GroupOfMovie(Movie(hasFile: false))).IsEqualTo(StatusGroupKind.Missing);
        await Assert.That(MediaStatusGrouping.GroupOfMovie(Movie(hasFile: true))).IsEqualTo(StatusGroupKind.Downloaded);
    }

    [Test]
    public async Task SeriesGroup_FoldsPartialIntoDownloaded_AndDownloadedMonitoredToo()
    {
        await Assert.That(MediaStatusGrouping.GroupOfSeries(Series("Missing"))).IsEqualTo(StatusGroupKind.Missing);
        await Assert.That(MediaStatusGrouping.GroupOfSeries(Series("Partial"))).IsEqualTo(StatusGroupKind.Downloaded);
        await Assert.That(MediaStatusGrouping.GroupOfSeries(Series("Downloaded"))).IsEqualTo(StatusGroupKind.Downloaded);
        await Assert.That(MediaStatusGrouping.GroupOfSeries(Series("Downloaded Monitored"))).IsEqualTo(StatusGroupKind.Downloaded);
        await Assert.That(MediaStatusGrouping.GroupOfSeries(Series("Missing", downloading: true))).IsEqualTo(StatusGroupKind.Downloading);
    }

    [Test]
    public async Task Movies_AreGroupedInCanonicalOrder_WithoutDuplicates()
    {
        List<MediaItem> items =
        [
            Movie(hasFile: true),                      // Downloaded
            Movie(hasFile: false, downloading: true),  // Downloading
            Movie(hasFile: false),                     // Missing
            Movie(hasFile: false, downloading: true)   // Downloading
        ];

        IReadOnlyList<StatusGroup<MediaItem>> groups = MediaStatusGrouping.ForMovies(items);

        await Assert.That(groups.Count).IsEqualTo(3);
        await Assert.That(groups[0].Kind).IsEqualTo(StatusGroupKind.Downloaded);
        await Assert.That(groups[1].Kind).IsEqualTo(StatusGroupKind.Downloading);
        await Assert.That(groups[2].Kind).IsEqualTo(StatusGroupKind.Missing);
        await Assert.That(groups[1].Count).IsEqualTo(2);
        await Assert.That(groups.Sum(g => g.Count)).IsEqualTo(items.Count);
    }

    [Test]
    public async Task Series_PartialFoldsIntoDownloaded_AndExposesSectionMetadata()
    {
        List<MediaItem> items = [Series("Downloaded"), Series("Missing"), Series("Partial")];

        IReadOnlyList<StatusGroup<MediaItem>> groups = MediaStatusGrouping.ForSeries(items);

        await Assert.That(groups.Count).IsEqualTo(2);
        await Assert.That(groups[0].Kind).IsEqualTo(StatusGroupKind.Downloaded);
        await Assert.That(groups[0].Count).IsEqualTo(2); // fully + partially downloaded
        await Assert.That(groups[1].Kind).IsEqualTo(StatusGroupKind.Missing);
        await Assert.That(groups.All(g => !string.IsNullOrEmpty(g.Title))).IsTrue();
        await Assert.That(groups.All(g => !string.IsNullOrEmpty(g.Icon))).IsTrue();
    }

    [Test]
    public async Task EmptyGroups_AreOmitted()
    {
        IReadOnlyList<StatusGroup<MediaItem>> groups = MediaStatusGrouping.ForMovies([Movie(hasFile: true)]);

        await Assert.That(groups.Count).IsEqualTo(1);
        await Assert.That(groups[0].Kind).IsEqualTo(StatusGroupKind.Downloaded);
    }

    [Test]
    public async Task SearchScore_RanksTitlePrefixAboveSubstringAboveOverview()
    {
        await Assert.That(MediaStatusGrouping.SearchScore(new MediaItem { Title = "Star Wars" }, "star")).IsEqualTo(3);
        await Assert.That(MediaStatusGrouping.SearchScore(new MediaItem { Title = "Lone Star" }, "star")).IsEqualTo(2);
        await Assert.That(MediaStatusGrouping.SearchScore(new MediaItem { Title = "Heat", Overview = "a lone star" }, "star")).IsEqualTo(1);
        await Assert.That(MediaStatusGrouping.SearchScore(new MediaItem { Title = "Heat" }, "star")).IsEqualTo(0);
    }

    [Test]
    public async Task OrderBySearchRelevance_PutsPrefixMatchesFirst_ThenTiesByTitle()
    {
        List<MediaItem> items =
        [
            new() { Title = "Lone Star" },                          // substring
            new() { Title = "Heat", Overview = "a lone star" },     // overview only
            new() { Title = "Star Wars" },                          // prefix
            new() { Title = "Star Trek" }                           // prefix
        ];

        IReadOnlyList<MediaItem> ordered = MediaStatusGrouping.OrderBySearchRelevance(items, "star");

        // Compare as a joined string so the order is what's asserted.
        await Assert.That(string.Join(" | ", ordered.Select(i => i.Title)))
            .IsEqualTo("Star Trek | Star Wars | Lone Star | Heat");
    }
}
