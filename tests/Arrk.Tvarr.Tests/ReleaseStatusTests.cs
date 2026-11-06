using Arrk.Tvarr.Components.Shared;
using Arrk.Ui.Components;
using Arrk.Core.Models;

namespace Arrk.Tvarr.Tests;

/// <summary>
/// Covers the single "Not Released" decision used by the media card status line, the card badge
/// and the media-popup badge: a movie is out once its web/physical window opens; a cinema-only
/// run, an unannounced title or a show that has not started airing still counts as unreleased.
/// </summary>
public class ReleaseStatusTests
{
    static readonly DateTime Past = DateTime.UtcNow.AddDays(-10);
    static readonly DateTime Future = DateTime.UtcNow.AddDays(10);

    [Test]
    public async Task Movie_UsesWebAndPhysicalWindows()
    {
        // Cinema-only and unannounced titles have not opened on web or physical media.
        await Assert.That(ReleaseStatus.MovieNotYetReleased(MovieStatus.InCinemas, Past, Future, Future)).IsTrue();
        await Assert.That(ReleaseStatus.MovieNotYetReleased(MovieStatus.InCinemas, Past, null, null)).IsTrue();
        await Assert.That(ReleaseStatus.MovieNotYetReleased(MovieStatus.Announced, null, null, null)).IsTrue();
        await Assert.That(ReleaseStatus.MovieNotYetReleased(MovieStatus.Tba, null, null, null)).IsTrue();

        // Once the web or physical window opens (or the movie is simply released), it is out.
        await Assert.That(ReleaseStatus.MovieNotYetReleased(MovieStatus.Released, Past, Past, null)).IsFalse();
        await Assert.That(ReleaseStatus.MovieNotYetReleased(MovieStatus.Released, Past, null, Past)).IsFalse();
        await Assert.That(ReleaseStatus.MovieNotYetReleased(MovieStatus.Released, null, null, null)).IsFalse();
    }

    [Test]
    public async Task Movie_FromTmdbStatusAndDates()
    {
        // TMDB says released but only the cinema window has opened: still not released.
        await Assert.That(ReleaseStatus.MovieNotYetReleased("Released", Past, null, null)).IsTrue();
        // A web or physical window in the past means it is out.
        await Assert.That(ReleaseStatus.MovieNotYetReleased("Released", null, Past, null)).IsFalse();
        await Assert.That(ReleaseStatus.MovieNotYetReleased("Released", null, null, Past)).IsFalse();
        // Explicitly unreleased TMDB statuses count.
        await Assert.That(ReleaseStatus.MovieNotYetReleased("In Production", null, null, null)).IsTrue();
        await Assert.That(ReleaseStatus.MovieNotYetReleased("Planned", null, null, null)).IsTrue();
        // Nothing to go on: never flagged.
        await Assert.That(ReleaseStatus.MovieNotYetReleased("Released", null, null, null)).IsFalse();
        await Assert.That(ReleaseStatus.MovieNotYetReleased("", null, null, null)).IsFalse();
        await Assert.That(ReleaseStatus.MovieNotYetReleased(null, null, null, null)).IsFalse();
    }

    [Test]
    public async Task Series_LibraryStatusAndFirstAir()
    {
        await Assert.That(ReleaseStatus.SeriesNotYetReleased(SeriesStatus.Upcoming, null)).IsTrue();
        await Assert.That(ReleaseStatus.SeriesNotYetReleased(SeriesStatus.Upcoming, Future)).IsTrue();
        await Assert.That(ReleaseStatus.SeriesNotYetReleased(SeriesStatus.Upcoming, Past)).IsFalse();
        await Assert.That(ReleaseStatus.SeriesNotYetReleased(SeriesStatus.Continuing, null)).IsFalse();
    }

    [Test]
    public async Task Series_FromTmdbStatusAndFirstAir()
    {
        await Assert.That(ReleaseStatus.SeriesNotYetReleased("Returning Series", Future)).IsTrue();
        await Assert.That(ReleaseStatus.SeriesNotYetReleased("Planned", null)).IsTrue();
        await Assert.That(ReleaseStatus.SeriesNotYetReleased("In Production", null)).IsTrue();
        await Assert.That(ReleaseStatus.SeriesNotYetReleased("Returning Series", Past)).IsFalse();
        await Assert.That(ReleaseStatus.SeriesNotYetReleased("Ended", Past)).IsFalse();
        await Assert.That(ReleaseStatus.SeriesNotYetReleased(null, null)).IsFalse();
    }

    [Test]
    public async Task MovieReleaseLabel_RefinesGenericReleased()
    {
        await Assert.That(ReleaseStatus.MovieReleaseLabel("Released", Past, Future, Future)).IsEqualTo("In Cinema");
        await Assert.That(ReleaseStatus.MovieReleaseLabel("Released", Past, Past, Future)).IsEqualTo("Web Release");
        await Assert.That(ReleaseStatus.MovieReleaseLabel("Released", Past, Past, Past)).IsEqualTo("Physical Release");
        await Assert.That(ReleaseStatus.MovieReleaseLabel("In Production", null, null, null)).IsEqualTo("In Production");
        await Assert.That(ReleaseStatus.MovieReleaseLabel(MovieStatus.Tba, null, null, null)).IsEqualTo("TBA");
    }
}
