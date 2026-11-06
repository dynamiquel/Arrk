using Arrk.Subtitles.Components.Shared;
using Arrk.Tv.Abstractions;

namespace Arrk.Subtitles.Tests;

/// <summary>
/// The subtitle actions share the item links regions with remote media popups. They must accept
/// the remote context without throwing, but only render for items that are actually in the library.
/// </summary>
public class SubtitleActionVisibilityTests
{
    [Test]
    public async Task MovieAction_OnlyTargetsLibraryMovies()
    {
        await Assert.That(MovieSubtitleActions.IsLibraryContext(new TvMovieActionContext(new TvMovie { Id = 7 }))).IsTrue();
        await Assert.That(MovieSubtitleActions.IsLibraryContext(new TvMediaLinkContext(TmdbId: 7, IsMovie: true))).IsFalse();
        await Assert.That(MovieSubtitleActions.IsLibraryContext(null)).IsFalse();
    }

    [Test]
    public async Task SeriesAction_OnlyTargetsLibrarySeries()
    {
        await Assert.That(SeriesSubtitleActions.IsLibraryContext(new TvSeriesActionContext(new TvSeries { Id = 7 }))).IsTrue();
        await Assert.That(SeriesSubtitleActions.IsLibraryContext(new TvMediaLinkContext(TmdbId: 7, IsMovie: false))).IsFalse();
        await Assert.That(SeriesSubtitleActions.IsLibraryContext(null)).IsFalse();
    }
}
