using Stackarr.Core.Models;
using Stackarr.Music.Lyrics;

namespace Stackarr.Music.Tests;

/// <summary>
/// Covers the lyrics matcher: turning Deezer metadata into provider queries, and refusing to bind a
/// same-named different song. The fixtures are real rows from a Deezer-tagged library.
/// </summary>
public class LyricsMatcherTests
{
    const string FrozenAlbum = "Frozen (Original Motion Picture Soundtrack)";
    const string Absolution = "Absolution";

    static LyricsCandidate Candidate(
        string track,
        string artist,
        double duration = 0,
        string album = "",
        bool synced = true,
        bool word = false,
        bool instrumental = false) => new()
        {
            TrackName = track,
            ArtistName = artist,
            AlbumName = album,
            DurationSeconds = duration,
            SyncedLyrics = synced ? "[00:01.00] line" : "",
            PlainLyrics = synced ? "" : "plain",
            HasWordSync = word,
            Instrumental = instrumental
        };

    // Queries

    [Test]
    public async Task BuildQueries_PrefersTheCleanedTitleAndTheAlbumArtist()
    {
        IReadOnlyList<LyricsQuery> queries =
            LyricsMatcher.BuildQueries("Robin Williams", "Disney", "Prince Ali (From \"Aladdin\"/Score)");

        // The disambiguation tail is gone, and the album artist is tried as well as the performer;
        // that is what rescues a soundtrack, where Deezer credits the singer.
        await Assert.That(queries[0].TrackName).IsEqualTo("Prince Ali");
        await Assert.That(queries[0].ArtistName).IsEqualTo("Robin Williams");
        await Assert.That(queries.Any(q => q.ArtistName == "Disney")).IsTrue();
        await Assert.That(queries.Any(q => q.Query is not null)).IsTrue();
    }

    [Test]
    public async Task BuildQueries_SkipsTheAlbumArtistWhenItIsTheSameAsThePerformer()
    {
        IReadOnlyList<LyricsQuery> queries = LyricsMatcher.BuildQueries("Muse", "muse", "Hysteria");

        // One artist-filtered query, one title-only query, one free-text fallback.
        await Assert.That(queries.Count(q => q.IsStructured)).IsEqualTo(1);
        await Assert.That(queries.Count(q => q.IsTitleOnly)).IsEqualTo(1);
        await Assert.That(queries.Count(q => q.Query is not null)).IsEqualTo(1);
    }

    [Test]
    public async Task BuildQueries_WithNoUsableArtist_AsksForTheTitleAlone()
    {
        IReadOnlyList<LyricsQuery> queries = LyricsMatcher.BuildQueries("Various Artists", "Various Artists", "Let It Go");

        await Assert.That(queries.Any(q => q.IsTitleOnly)).IsTrue();
        await Assert.That(queries.Any(q => q.ArtistName == "Various Artists")).IsFalse();
    }

    [Test]
    public async Task DistinctArtists_DropsPlaceholderCredits()
    {
        // The Frozen soundtrack credits "Various Artists"; searching LRCLIB filtered by that both
        // fails (503) and can never match, so it must not reach the query plan.
        await Assert.That(LyricsMatcher.DistinctArtists("Various Artists", "Various Artists").Count).IsEqualTo(0);

        IReadOnlyList<string> real = LyricsMatcher.DistinctArtists("Robin Williams", "Disney");
        await Assert.That(real.Count).IsEqualTo(2);
    }

    [Test]
    public async Task IsUsableArtist_RecognisesPlaceholders()
    {
        await Assert.That(LyricsMatcher.IsUsableArtist("Various Artists")).IsFalse();
        await Assert.That(LyricsMatcher.IsUsableArtist("Various Artist")).IsFalse();
        await Assert.That(LyricsMatcher.IsUsableArtist("Unknown Artist")).IsFalse();
        await Assert.That(LyricsMatcher.IsUsableArtist("")).IsFalse();
        await Assert.That(LyricsMatcher.IsUsableArtist("Idina Menzel")).IsTrue();
    }

    // Verification

    [Test]
    public async Task PickBest_AcceptsAnExactMatch()
    {
        LyricsCandidate? best = LyricsMatcher.PickBest(
            [Candidate("Hysteria", "Muse", 213, Absolution)], ["Muse"], "Hysteria", Absolution, 213);

        await Assert.That(best).IsNotNull();
        await Assert.That(best!.TrackName).IsEqualTo("Hysteria");
        await Assert.That(best.ArtistName).IsEqualTo("Muse");
    }

    [Test]
    public async Task PickBest_RejectsADifferentArtistWithTheSameTitle()
    {
        // A tribute/karaoke credit must never lend its lyrics to the song we hold.
        LyricsCandidate? best = LyricsMatcher.PickBest(
            [Candidate("Hysteria", "Some Tribute Band", 213, Absolution)], ["Muse"], "Hysteria", Absolution, 213);

        await Assert.That(best).IsNull();
    }

    [Test]
    public async Task PickBest_MatchesThroughEditionJunkInTheTitle()
    {
        // We hold "The Chain (2004 Remaster)"; the provider has the plain title.
        LyricsCandidate? best = LyricsMatcher.PickBest(
            [Candidate("The Chain", "Fleetwood Mac", 268, "Rumours")], ["Fleetwood Mac"], "The Chain (2004 Remaster)", "Rumours", 268);

        await Assert.That(best).IsNotNull();
        await Assert.That(best!.TrackName).IsEqualTo("The Chain");
    }

    [Test]
    public async Task PickBest_RejectsAFuzzyMatchWithTheWrongDuration()
    {
        // "Wild Horses" is a common title; a different recording must be rejected.
        LyricsCandidate? best = LyricsMatcher.PickBest(
            [Candidate("Wild Horses", "Chris Janson", 380, "Wild Horses")], ["Chris Janson"], "Wild Horses", "Wild Horses", 189);

        await Assert.That(best).IsNull();
    }

    [Test]
    public async Task PickBest_AcceptsAnExactMatchAcrossADifferentMaster()
    {
        // An identical title+artist tolerates a wider duration gap (radio edit vs album version).
        LyricsCandidate? best = LyricsMatcher.PickBest(
            [Candidate("Boys Don't Cry", "The Cure", 175, "Greatest Hits")], ["The Cure"], "Boys Don't Cry", "Greatest Hits", 156);

        await Assert.That(best).IsNotNull();
        await Assert.That(best!.TrackName).IsEqualTo("Boys Don't Cry");
    }

    [Test]
    public async Task PickBest_PrefersWordSyncThenSynced()
    {
        LyricsCandidate? best = LyricsMatcher.PickBest(
            [
                Candidate("Hysteria", "Muse", 213, Absolution),
                Candidate("Hysteria", "Muse", 213, Absolution, word: true),
                Candidate("Hysteria", "Muse", 213, Absolution, synced: false)
            ],
            ["Muse"], "Hysteria", Absolution, 213);

        await Assert.That(best).IsNotNull();
        await Assert.That(best!.HasWordSync).IsTrue();
    }

    [Test]
    public async Task PickBest_HonoursTheAccentedApostropheInDeezerTitles()
    {
        // Deezer stores "I'm Not In Love" with a curly apostrophe.
        LyricsCandidate? best = LyricsMatcher.PickBest(
            [Candidate("I'm Not in Love", "10cc", 364, "The Original Soundtrack")], ["10cc"], "I’m Not In Love", "The Original Soundtrack", 364);

        await Assert.That(best).IsNotNull();
        await Assert.That(best!.ArtistName).IsEqualTo("10cc");
    }

    [Test]
    public async Task PickBest_IgnoresCandidatesWithNoLyrics()
    {
        LyricsCandidate empty = new() { TrackName = "Hysteria", ArtistName = "Muse", AlbumName = Absolution, DurationSeconds = 213 };
        LyricsCandidate? best = LyricsMatcher.PickBest([empty], ["Muse"], "Hysteria", Absolution, 213);
        await Assert.That(best).IsNull();
    }

    // Unverified credits

    [Test]
    public async Task PickBest_OnlyAcceptsAnUnverifiedCreditOnTheAlbumAndDuration()
    {
        // With a real artist to check against, a record credited elsewhere is not artist-verified; it
        // is only usable when its album corroborates the identity.
        LyricsCandidate? sameAlbum = LyricsMatcher.PickBest(
            [Candidate("Hysteria", "", 213, Absolution)], ["Muse"], "Hysteria", Absolution, 213);
        await Assert.That(sameAlbum).IsNotNull();

        LyricsCandidate? otherAlbum = LyricsMatcher.PickBest(
            [Candidate("Hysteria", "", 213, "A Different Record")], ["Muse"], "Hysteria", Absolution, 213);
        await Assert.That(otherAlbum).IsNull();
    }

    [Test]
    public async Task PickBest_RejectsACommonTitleFromAnotherAlbumEvenWhenTheDurationIsCloser()
    {
        // The real failure: "Let It Go" is a title many different songs share. A stranger's version at
        // an *almost identical* length must lose to the correct song, and a wrong album must be
        // refused outright, a title plus a duration is not an identity.
        List<LyricsCandidate> pool =
        [
            Candidate("Let It Go", "Some Other Band", 224.5, "Some Other Album", synced: true),
            Candidate("Let It Go", "Idina Menzel", 225, FrozenAlbum, synced: true)
        ];

        LyricsCandidate? best = LyricsMatcher.PickBest(
            pool, [], "Let It Go (From \"Frozen\"/Soundtrack Version)", FrozenAlbum, 224);

        await Assert.That(best).IsNotNull();
        await Assert.That(best!.ArtistName).IsEqualTo("Idina Menzel");

        // …and with only the stranger present, nothing is chosen at all.
        await Assert.That(LyricsMatcher.PickBest(
            [pool[0]], [], "Let It Go (From \"Frozen\"/Soundtrack Version)", FrozenAlbum, 224)).IsNull();
    }

    [Test]
    public async Task PickBest_WithNoAlbum_HasNoIdentityLeftForAnUnverifiedCredit()
    {
        LyricsCandidate? best = LyricsMatcher.PickBest(
            [Candidate("Let It Go", "Some Other Band", 224.5, "Some Other Album")], [], "Let It Go", album: "", 224);

        await Assert.That(best).IsNull();
    }

    [Test]
    public async Task PickBest_AcceptsASoundtrackOnAnExactTitleAndMatchingAlbum()
    {
        // Our copy is credited "Various Artists"; LRCLIB credits the singer under the same album.
        LyricsCandidate? best = LyricsMatcher.PickBest(
            [Candidate("Let It Go", "Idina Menzel", 225, FrozenAlbum)], [], "Let It Go (From \"Frozen\"/Soundtrack Version)", FrozenAlbum, 224);

        await Assert.That(best).IsNotNull();
        await Assert.That(best!.ArtistName).IsEqualTo("Idina Menzel");
    }

    [Test]
    public async Task PickBest_RejectsACompactDiscKaraokeCredit()
    {
        // Karaoke versions are an exact title on a similarly-named release at an almost identical
        // length, so the album check alone would not save us.
        LyricsCandidate? best = LyricsMatcher.PickBest(
            [Candidate("Let It Go", "Instrumental Karaoke", 225, "Karaoke Hits")], [], "Let It Go", FrozenAlbum, 224);

        await Assert.That(best).IsNull();
    }

    // Content

    [Test]
    public async Task ToContent_WritesSyncedLyricsAsLrc()
    {
        LyricsContent? content = LyricsMatcher.ToContent(Candidate("Hysteria", "Muse", 213, Absolution));
        await Assert.That(content).IsNotNull();
        await Assert.That(content!.Extension).IsEqualTo(".lrc");
        await Assert.That(content.Tier).IsEqualTo(LyricsTier.Line);
    }

    [Test]
    public async Task ToContent_WritesPlainLyricsAsText()
    {
        LyricsContent? content = LyricsMatcher.ToContent(Candidate("Hysteria", "Muse", 213, Absolution, synced: false));
        await Assert.That(content).IsNotNull();
        await Assert.That(content!.Extension).IsEqualTo(".txt");
        await Assert.That(content.Tier).IsEqualTo(LyricsTier.Plain);
    }

    [Test]
    public async Task ToContent_NeverAdvertisesWordSyncForAnLrcSidecar()
    {
        // Word-level timing would need an ELRC/TTML file; claiming the tier from an LRC would make
        // the player try to fill words it has no timings for.
        LyricsContent? content = LyricsMatcher.ToContent(Candidate("Hysteria", "Muse", 213, Absolution, word: true));
        await Assert.That(content!.Tier).IsEqualTo(LyricsTier.Line);
    }

    [Test]
    public async Task ToContent_ReturnsNothingForAnInstrumental()
    {
        LyricsContent? content = LyricsMatcher.ToContent(Candidate("Neon Night", "Halo", 337, "Halo 3: ODST", instrumental: true));
        await Assert.That(content).IsNull();
    }
}
