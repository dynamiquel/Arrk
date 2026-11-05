using Stackarr.Core.Playback;

namespace Stackarr.Core.Tests.Playback;

/// <summary>
/// Covers the shared media identity keys. Producers (players) and consumers (staleness, watchlists)
/// must agree on these strings, so a change here is a breaking change to persisted history.
/// </summary>
public class MediaItemKeysTests
{
    [Test]
    public async Task MovieAndSeriesKeys_UseProviderIds()
    {
        await Assert.That(MediaItemKeys.Movie(1273221)).IsEqualTo("tv:movie:tmdb:1273221");
        await Assert.That(MediaItemKeys.Movie("42")).IsEqualTo("tv:movie:tmdb:42");
        await Assert.That(MediaItemKeys.Movie("not-a-number")).IsEqualTo("");
        await Assert.That(MediaItemKeys.Series(305089, 0)).IsEqualTo("tv:series:tvdb:305089");
        await Assert.That(MediaItemKeys.Series(0, 42)).IsEqualTo("tv:series:tmdb:42");
        await Assert.That(MediaItemKeys.Episode("tv:series:tvdb:305089", 2, 4))
            .IsEqualTo("tv:series:tvdb:305089:s2e4");
    }

    [Test]
    public async Task SeriesKeyHierarchy_ClassifiesSeriesSeasonAndEpisode()
    {
        const string series = "tv:series:tvdb:305089";

        await Assert.That(MediaItemKeys.TryGetSeriesHierarchy(series, out MediaKeyHierarchy show)).IsTrue();
        await Assert.That(show.SeriesKey).IsEqualTo(series);
        await Assert.That(show.SeasonKey).IsNull();
        await Assert.That(show.IncludesChildren).IsTrue();

        await Assert.That(MediaItemKeys.TryGetSeriesHierarchy(
            MediaItemKeys.Season(series, 2), out MediaKeyHierarchy season)).IsTrue();
        await Assert.That(season.SeriesKey).IsEqualTo(series);
        await Assert.That(season.SeasonKey).IsEqualTo($"{series}:s2");
        await Assert.That(season.IncludesChildren).IsTrue();

        await Assert.That(MediaItemKeys.TryGetSeriesHierarchy(
            MediaItemKeys.Episode(series, 2, 4), out MediaKeyHierarchy episode)).IsTrue();
        await Assert.That(episode.SeriesKey).IsEqualTo(series);
        await Assert.That(episode.SeasonKey).IsEqualTo($"{series}:s2");
        await Assert.That(episode.IncludesChildren).IsFalse();

        await Assert.That(MediaItemKeys.TryGetSeriesHierarchy("tv:movie:tmdb:1", out _)).IsFalse();
    }

    [Test]
    public async Task MusicAlbumKey_NormalisesArtistAndTitle()
    {
        string key = MediaItemKeys.MusicAlbum("Various Artists", "101 Dalmatians");

        await Assert.That(key).IsEqualTo("music:album:variousartists|101dalmatians");
        await Assert.That(MediaItemKeys.MusicAlbum("Various Artists", "101 Dalmatians")).IsEqualTo(key);
    }

    [Test]
    public async Task PlaybackDevice_ParsesBrowserUserAgent()
    {
        (string? device, string? client) = PlaybackDevice.FromUserAgent(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36");

        await Assert.That(device).IsEqualTo("Windows");
        await Assert.That(client).IsEqualTo("Chrome");

        (string? jellyfinDevice, string? jellyfinClient) = PlaybackDevice.FromUserAgent("Jellyfin/1.0");
        await Assert.That(jellyfinClient).IsEqualTo("Jellyfin");
        await Assert.That(jellyfinDevice).IsNull();
    }
}
