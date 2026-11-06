using Microsoft.Extensions.Logging.Abstractions;
using Arrk.Core.Models;
using Arrk.Music.Lyrics;

namespace Arrk.Music.Tests;

/// <summary>
/// Optional live smoke test against the real LRCLIB. It only runs when <c>ARRK_LIVE_LYRICS=1</c>
/// (and never when <c>ARRK_SKIP_LIVE_TESTS=1</c>), so CI stays hermetic. It proves the provider
/// contract the whole feature rests on, that a well-known track resolves to synced lyrics and that
/// the matcher accepts them, rather than asserting a coverage percentage (LRCLIB is a community
/// database and its holdings change).
/// </summary>
public class LyricsLiveSmokeTests
{
    static LrclibClient? Live()
    {
        if (Environment.GetEnvironmentVariable("ARRK_SKIP_LIVE_TESTS") == "1") return null;
        if (Environment.GetEnvironmentVariable("ARRK_LIVE_LYRICS") != "1") return null;
        return new LrclibClient(NullLogger<LrclibClient>.Instance);
    }

    [Test]
    public async Task Get_ForAKnownTrack_ReturnsSyncedLyrics()
    {
        LrclibClient? client = Live();
        if (client is null) return;

        LyricsCandidate? candidate = await client.GetAsync("Muse", "Hysteria", album: null, durationSeconds: 213);

        await Assert.That(candidate).IsNotNull();
        await Assert.That(candidate!.SyncedLyrics).Contains("[");
        await Assert.That(candidate.PlainLyrics.Length > 0 || candidate.SyncedLyrics.Length > 0).IsTrue();
    }

    [Test]
    public async Task Search_AndMatcher_ResolveADeezerStyleTitleWithAJunkTail()
    {
        LrclibClient? client = Live();
        if (client is null) return;

        // What the library actually stores: Deezer appends the edition to the title.
        List<LyricsCandidate> candidates = await client.SearchAsync("The Chain", "Fleetwood Mac");
        LyricsCandidate? best = LyricsMatcher.PickBest(candidates, ["Fleetwood Mac"], "The Chain (2004 Remaster)", "Rumours", 268);

        await Assert.That(best).IsNotNull();
    }

    [Test]
    public async Task ToContent_ForAKnownTrack_ProducesAnLrcSidecar()
    {
        LrclibClient? client = Live();
        if (client is null) return;

        LyricsCandidate? candidate = await client.GetAsync("Muse", "Hysteria", album: null, durationSeconds: 213);
        LyricsContent? content = candidate is null ? null : LyricsMatcher.ToContent(candidate);

        await Assert.That(content).IsNotNull();
        await Assert.That(content!.Extension).IsEqualTo(".lrc");
        await Assert.That(content.Tier).IsEqualTo(LyricsTier.Line);
    }

    [Test]
    public async Task Lookup_ForASoundtrackCreditedToVariousArtists_Resolves()
    {
        LrclibClient? client = Live();
        if (client is null) return;

        // The reported failure, end to end against the real API: the library credits "Various Artists",
        // which LRCLIB answers with a 503 and can never match, so the title is searched alone and
        // accepted on an exact title plus a matching duration.
        const string title = "Let It Go (From \"Frozen\"/Soundtrack Version)";
        IReadOnlyList<string> artists = LyricsMatcher.DistinctArtists("Various Artists", "Various Artists");
        await Assert.That(artists.Count).IsEqualTo(0);

        List<LyricsCandidate> pool = [];
        foreach (LyricsQuery query in LyricsMatcher.BuildQueries("Various Artists", "Various Artists", title))
        {
            pool.AddRange(query.IsStructured
                ? await client.SearchAsync(query.TrackName, query.ArtistName)
                : query.IsTitleOnly
                    ? await client.SearchAsync(query.TrackName)
                    : await client.SearchAsync(query: query.Query));
        }

        LyricsCandidate? best = LyricsMatcher.PickBest(pool, artists, title, "Frozen (Original Motion Picture Soundtrack)", 224);

        await Assert.That(best).IsNotNull();
        // The album is what identifies the song when the credit is a placeholder, and it is what
        // stops a different song titled "Let It Go" (there are many) from winning on duration alone.
        await Assert.That(best!.AlbumName).Contains("Frozen", StringComparison.OrdinalIgnoreCase);
        await Assert.That(best.ArtistName.ToLowerInvariant()).DoesNotContain("karaoke");
        await Assert.That(LyricsMatcher.ToContent(best)).IsNotNull();
    }
}
