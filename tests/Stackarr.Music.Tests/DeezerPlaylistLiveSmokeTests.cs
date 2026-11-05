using Microsoft.Extensions.Logging.Abstractions;
using Stackarr.Music.Models;
using Stackarr.Music.Services;

namespace Stackarr.Music.Tests;

/// <summary>
/// Optional live smoke tests against the real, keyless Deezer API. They only run when
/// <c>STACKARR_LIVE_DEEZER=1</c> is set (and never when <c>STACKARR_SKIP_LIVE_TESTS=1</c>), so normal
/// CI stays hermetic. They verify the playlist-discovery endpoints the Discover → Playlists tab,
/// the per-genre shelves and the search bar rely on, end-to-end against Deezer's real payloads.
/// </summary>
public class DeezerPlaylistLiveSmokeTests
{
    static DeezerService? Live()
    {
        if (Environment.GetEnvironmentVariable("STACKARR_SKIP_LIVE_TESTS") == "1") return null;
        if (Environment.GetEnvironmentVariable("STACKARR_LIVE_DEEZER") != "1") return null;
        return new DeezerService(NullLogger<DeezerService>.Instance);
    }

    [Test]
    public async Task FeaturedPlaylists_Live_ReturnsMoreThanTheOldLimit()
    {
        DeezerService? deezer = Live();
        if (deezer is null) return;

        List<MusicItem> playlists = await deezer.GetChartPlaylistsAsync();

        // The Playlists tab shows Deezer's full featured set.
        await Assert.That(playlists.Count).IsGreaterThan(30);
        await Assert.That(playlists.All(p => p.EntityType == MusicEntityType.Playlist)).IsTrue();
        await Assert.That(playlists.All(p => p.DeezerId > 0)).IsTrue();
        await Assert.That(playlists.Any(p => !string.IsNullOrEmpty(p.ArtUrl))).IsTrue();
    }

    [Test]
    public async Task GenrePlaylists_Live_ReturnsPopEditorialPlaylists()
    {
        DeezerService? deezer = Live();
        if (deezer is null) return;

        // Genre 132 is Deezer's Pop genre.
        List<MusicItem> playlists = await deezer.GetGenrePlaylistsAsync(132, 100);

        await Assert.That(playlists.Count).IsGreaterThan(0);
        await Assert.That(playlists.All(p => p.EntityType == MusicEntityType.Playlist)).IsTrue();
        await Assert.That(playlists.All(p => p.DeezerId > 0)).IsTrue();
    }

    [Test]
    public async Task PlaylistSearch_Live_FindsTopUkChart()
    {
        DeezerService? deezer = Live();
        if (deezer is null) return;

        List<MusicItem> playlists = await deezer.SearchPlaylistsAsync("Top UK", 10);

        await Assert.That(playlists.Count).IsGreaterThan(0);
        await Assert.That(playlists.Any(p => p.Title.Contains("UK", StringComparison.OrdinalIgnoreCase))).IsTrue();
    }
}
