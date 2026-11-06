using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Arrk.Core;
using Arrk.Core.Events;
using Arrk.Core.Models;
using Arrk.Core.Notifications;
using Arrk.Music.Models;
using Arrk.Music.Services;
using Arrk.TestSupport;

namespace Arrk.Music.Tests;

/// <summary>
/// Verifies Arrk's native music acquisition: title-first search (not Lidarr's broken
/// <c>"{artist} {album}"</c> query), scoring/junk rejection, the single→containing-album fallback,
/// and the Transmission grab / native import hand-off.
/// </summary>
public class MusicAcquisitionServiceTests
{
    const string ODonnell = "Martin O’Donnell";

    static string SearchJson(params object[] releases) => JsonSerializer.Serialize(releases);

    static object Release(string title, int seeders, string? guid = null, string? infoHash = null,
        string indexer = "The Pirate Bay", long size = 200_000_000) => new
    {
        title,
        size,
        seeders,
        indexer,
        protocol = "torrent",
        publishDate = "2009-09-25T17:17:23Z",
        guid = guid ?? $"magnet:?xt=urn:btih:{infoHash ?? Guid.NewGuid().ToString("N").ToUpperInvariant()}",
        infoHash = infoHash ?? "",
        downloadUrl = "http://prowlarr/download"
    };

    static (MusicAcquisitionService Acquisition, FakeHttpMessageHandler Prowlarr, FakeHttpMessageHandler Transmission, TestMusicDb Db) Build(
        Func<HttpRequestMessage, string>? prowlarrResponder = null,
        Func<HttpRequestMessage, string>? transmissionResponder = null,
        bool slskdEnabled = false,
        FakeHttpMessageHandler? slskdHandler = null,
        FakeHttpMessageHandler? deezerHandler = null,
        IConfiguration? configuration = null,
        MusicNotifications? notifications = null)
    {
        TestMusicDb db = new();
        MusicLibrary library = MusicTestHelpers.Library(db);

        FakeHttpMessageHandler prowlarrHandler = new();
        prowlarrHandler.Fallback = (req, _) => Task.FromResult(FakeHttpMessageHandler.JsonResponse(
            prowlarrResponder?.Invoke(req) ?? "[]"));

        FakeHttpMessageHandler transmissionHandler = new();
        transmissionHandler.Fallback = (req, _) => Task.FromResult(FakeHttpMessageHandler.JsonResponse(
            transmissionResponder?.Invoke(req) ?? TransmissionOk()));

        MusicAcquisitionService acquisition = MusicTestHelpers.Acquisition(
            library, db, prowlarrHandler, transmissionHandler, slskdHandler, slskdEnabled,
            deezerHandler: deezerHandler, configuration: configuration, notifications: notifications);
        return (acquisition, prowlarrHandler, transmissionHandler, db);
    }

    static IConfiguration SourcesConfig(bool torrents, bool soulseek) =>
        new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["services:music:useTorrents"] = torrents ? "true" : "false",
                ["services:music:useSoulseek"] = soulseek ? "true" : "false"
            }).Build();

    static FakeHttpMessageHandler DeezerAlbum(long albumId, string title, string artist, params (string Title, int Number)[] tracks)
    {
        string searchJson = JsonSerializer.Serialize(new
        {
            data = new[] { new { id = albumId, title, artist = new { id = 9, name = artist } } }
        });
        string albumJson = JsonSerializer.Serialize(new
        {
            id = albumId,
            title,
            artist = new { id = 9, name = artist },
            nb_tracks = tracks.Length,
            record_type = "album"
        });
        string tracksJson = JsonSerializer.Serialize(new
        {
            data = tracks.Select(t => new { id = (long)t.Number, title = t.Title, track_position = t.Number, disk_number = 1 })
        });

        FakeHttpMessageHandler handler = new();
        handler.Fallback = (req, _) =>
        {
            string path = req.RequestUri!.AbsolutePath;
            string json = path.Contains("search/album")
                ? searchJson
                : path.Contains($"/album/{albumId}/tracks")
                    ? tracksJson
                    : albumJson;
            return Task.FromResult(FakeHttpMessageHandler.JsonResponse(json));
        };
        return handler;
    }

    static string TransmissionOk(string argumentsJson = "{}") =>
        $$"""{ "result": "success", "arguments": {{argumentsJson}} }""";

    // Search + scoring

    [Test]
    public async Task Search_PicksRelevantAlbum_AndUsesAudioCategories()
    {
        (MusicAcquisitionService acquisition, FakeHttpMessageHandler prowlarr, _, _) = Build(
            prowlarrResponder: req => req.RequestUri!.AbsolutePath.Contains("search")
                ? SearchJson(
                    Release("Halo 3 - ODST - OST (Kingdom Music) 2009", seeders: 2),
                    Release("Halo 3 OST (Original Soundtrack)", seeders: 1),
                    Release("Laurel Halo - Quarantine (2012) [FLAC]", seeders: 3),
                    Release("Halo 3 ODST Karaoke Hits", seeders: 99))
                : "[]");

        List<MusicRelease> releases = await acquisition.SearchReleasesAsync(ODonnell, "Halo 3: ODST: Original Soundtrack");

        await Assert.That(releases).IsNotEmpty();
        await Assert.That(releases[0].Title).IsEqualTo("Halo 3 - ODST - OST (Kingdom Music) 2009");
        await Assert.That(releases.Any(r => r.Title.Contains("Karaoke", StringComparison.OrdinalIgnoreCase))).IsFalse();
        await Assert.That(releases.Any(r => r.Title.Contains("Laurel Halo", StringComparison.OrdinalIgnoreCase))).IsFalse();
        await Assert.That(prowlarr.Requests.All(r => r.Uri.Query.Contains("categories=3000"))).IsTrue();
    }

    [Test]
    public async Task Search_EmptyQuery_DoesNotHitNetwork()
    {
        (MusicAcquisitionService acquisition, FakeHttpMessageHandler prowlarr, _, _) = Build();

        List<MusicRelease> releases = await acquisition.SearchReleasesAsync(ODonnell, "");

        await Assert.That(releases).IsEmpty();
        await Assert.That(prowlarr.Requests).IsEmpty();
    }

    [Test]
    public async Task Search_PrefersLossless_OnTie()
    {
        (MusicAcquisitionService acquisition, _, _, _) = Build(
            prowlarrResponder: _ => SearchJson(
                Release("Ella Langley - Dandelion (2026) [MP3 320]", seeders: 10),
                Release("Ella Langley - Dandelion (2026) [Flac 24-48]", seeders: 10)));

        List<MusicRelease> releases = await acquisition.SearchReleasesAsync("Ella Langley", "Dandelion");

        await Assert.That(releases[0].IsLossless).IsTrue();
        await Assert.That(releases[0].Title).Contains("Flac");
    }

    [Test]
    public async Task Search_ExcludesMonoReleases_AndKeepsStereo()
    {
        (MusicAcquisitionService acquisition, _, _, _) = Build(
            prowlarrResponder: _ => SearchJson(
                Release("Ella Langley - Dandelion (2026) [Mono] [FLAC]", seeders: 30),
                Release("Ella Langley - Dandelion (2026) [Stereo] [FLAC]", seeders: 5)));

        List<MusicRelease> releases = await acquisition.SearchReleasesAsync("Ella Langley", "Dandelion");

        await Assert.That(releases).IsNotEmpty();
        await Assert.That(releases.Any(r => r.Title.Contains("Mono", StringComparison.OrdinalIgnoreCase))).IsFalse();
        await Assert.That(releases.Any(r => r.Title.Contains("Stereo", StringComparison.OrdinalIgnoreCase))).IsTrue();
    }

    [Test]
    public async Task Search_KeepsMonoTitledRelease_WhenTheArtistItselfIsMono()
    {
        (MusicAcquisitionService acquisition, _, _, _) = Build(
            prowlarrResponder: _ => SearchJson(
                Release("Mono - Pilgrimage (2006) [FLAC]", seeders: 4)));

        List<MusicRelease> releases = await acquisition.SearchReleasesAsync("Mono", "Pilgrimage");

        await Assert.That(releases).IsNotEmpty();
        await Assert.That(releases[0].IsJunk).IsFalse();
    }

    [Test]
    public async Task IsMonoRelease_MatchesMonoMarkers_ButNotWordsContainingMono()
    {
        await Assert.That(MusicAcquisitionService.IsMonoRelease("Album (Mono)", "Album")).IsTrue();
        await Assert.That(MusicAcquisitionService.IsMonoRelease("Album [MONO] Edition", "Album")).IsTrue();
        await Assert.That(MusicAcquisitionService.IsMonoRelease("Album (Monaural)", "Album")).IsTrue();
        // Words that merely contain the letters, and a mono request/artist, are never filtered.
        await Assert.That(MusicAcquisitionService.IsMonoRelease("Album Monologue", "Album")).IsFalse();
        await Assert.That(MusicAcquisitionService.IsMonoRelease("Artist - Album (Mono)", "Mono")).IsFalse();
        await Assert.That(MusicAcquisitionService.IsMonoRelease("Mono - Album", "Album", "Mono")).IsFalse();
    }

    [Test]
    public async Task SearchTrack_RequiresArtist_SoGenericTitleDoesNotMatchJunk()
    {
        (MusicAcquisitionService acquisition, _, _, _) = Build(
            prowlarrResponder: _ => SearchJson(
                Release("Boston - Greatest Hits [FLAC]", seeders: 50),
                Release("Boston (2015) 1080p BluRay", seeders: 40),
                Release("STELLA LEFTY - Boston (2026) [FLAC]", seeders: 3)));

        List<MusicRelease> results = await acquisition.SearchReleasesAsync("STELLA LEFTY", "Boston", requireArtist: true);

        await Assert.That(results).IsNotEmpty();
        await Assert.That(results.All(r => r.ArtistMatched)).IsTrue();
        await Assert.That(results.Any(r => r.Title.Contains("STELLA LEFTY"))).IsTrue();
        await Assert.That(results.Any(r => r.Title.StartsWith("Boston -"))).IsFalse();
    }

    [Test]
    public async Task SearchAlbum_DoesNotRequireArtistCredit()
    {
        (MusicAcquisitionService acquisition, _, _, _) = Build(
            prowlarrResponder: _ => SearchJson(
                Release("Halo 3 OST (Original Soundtrack)", seeders: 5)));

        List<MusicRelease> releases = await acquisition.SearchReleasesAsync("Martin O’Donnell", "Halo 3: Original Soundtrack");

        await Assert.That(releases).IsNotEmpty();
        await Assert.That(releases[0].Title).Contains("Halo 3 OST");
    }

    [Test]
    public async Task Search_RejectsUnrelatedReleases_ThatShareOneWord()
    {
        (MusicAcquisitionService acquisition, _, _, _) = Build(
            prowlarrResponder: _ => SearchJson(
                Release("Halo 3 - ODST - OST (Kingdom Music) 2009", seeders: 2),
                Release("Some Japanese Halo Collection 2004", seeders: 99)));

        List<MusicRelease> releases = await acquisition.SearchReleasesAsync("Martin O’Donnell", "Halo 3: ODST: Original Soundtrack");

        await Assert.That(releases).IsNotEmpty();
        await Assert.That(releases.Any(r => r.Title.Contains("Japanese", StringComparison.OrdinalIgnoreCase))).IsFalse();
    }

    [Test]
    public async Task Search_RejectsReleaseWithScatteredTokens()
    {
        (MusicAcquisitionService acquisition, _, _, _) = Build(
            prowlarrResponder: _ => SearchJson(
                Release("Tomotaka Osumi Mobile Suit Gundam U C Engage Original Motion Picture Soundtrack 3 Angel Halo Axis Edited 2024", seeders: 99),
                Release("Halo 3 OST (Original Soundtrack)", seeders: 2)));

        List<MusicRelease> releases = await acquisition.SearchReleasesAsync("Martin O’Donnell", "Halo 3 (Original Soundtrack)");

        await Assert.That(releases.Any(r => r.Title.Contains("Gundam", StringComparison.OrdinalIgnoreCase))).IsFalse();
        await Assert.That(releases.Any(r => r.Title.Contains("Halo 3 OST", StringComparison.OrdinalIgnoreCase))).IsTrue();
    }

    [Test]
    public async Task Search_MatchesSymbolTitle_WhenIndexerSpellsItOut()
    {
        (MusicAcquisitionService acquisition, _, _, _) = Build(
            prowlarrResponder: _ => SearchJson(
                Release("Ed Sheeran - divide (Deluxe) (2017) FLAC", seeders: 10)));

        List<MusicRelease> releases = await acquisition.SearchReleasesAsync("Ed Sheeran", "÷ (Deluxe)");

        await Assert.That(releases).IsNotEmpty();
        await Assert.That(releases[0].Title).Contains("divide");
    }

    [Test]
    public async Task Search_StripsTrackDisambiguationSuffix()
    {
        // Deezer titles carry a "(From .../Soundtrack Version)" tail that no release name has; the
        // clean title must be what is searched and scored, or soundtrack songs never match.
        (MusicAcquisitionService acquisition, FakeHttpMessageHandler prowlarr, _, _) = Build(
            prowlarrResponder: req => req.RequestUri!.AbsolutePath.Contains("search")
                ? SearchJson(Release("Idina Menzel - Let It Go [FLAC]", seeders: 12))
                : "[]");

        List<MusicRelease> releases = await acquisition.SearchReleasesAsync(
            "Idina Menzel", "Let It Go (From \"Frozen\"/Soundtrack Version)", requireArtist: true);

        await Assert.That(releases).IsNotEmpty();
        await Assert.That(releases[0].Title).Contains("Let It Go");
        await Assert.That(prowlarr.Requests.All(r =>
            !Uri.UnescapeDataString(r.Uri.Query).Contains("Soundtrack Version", StringComparison.OrdinalIgnoreCase))).IsTrue();
    }

    [Test]
    public async Task SearchTrack_Soulseek_MatchesOnCleanTitle_AndSearchesOnce()
    {
        FakeHttpMessageHandler slskdHandler = new();
        slskdHandler.Fallback = (req, _) =>
        {
            string path = req.RequestUri!.AbsolutePath;
            string json = path.Contains("/session")
                ? """{ "token": "t" }"""
                : path.EndsWith("/server")
                    ? """{ "isLoggedIn": true }"""
                    : path.Contains("/transfers/downloads/")
                        ? """{ "ok": true }"""
                        : path.Contains("/searches")
                            ? """
                              { "id": "s1", "isComplete": true, "responses": [
                                { "username": "peer1", "hasFreeUploadSlot": true, "queueLength": 0, "files": [
                                  { "filename": "Music\\Disney\\Idina Menzel - Let It Go.flac", "size": 30000000, "bitRate": 0 } ] } ] }
                              """
                            : "[]";
            return Task.FromResult(FakeHttpMessageHandler.JsonResponse(json));
        };

        (MusicAcquisitionService acquisition, _, _, _) = Build(slskdEnabled: true, slskdHandler: slskdHandler);

        MusicGrabResult result = await acquisition.SearchTrackAndGrabAsync(
            "Idina Menzel", "Let It Go (From \"Frozen\"/Soundtrack Version)");

        await Assert.That(result.Success).IsTrue();
        List<CapturedRequest> searches = slskdHandler.Requests
            .Where(r => r.Method.Method == "POST" && r.Uri.AbsolutePath.EndsWith("/api/v0/searches"))
            .ToList();
        await Assert.That(searches).HasCount(1);
        await Assert.That(searches[0].Body).Contains("Let It Go");
        await Assert.That(searches[0].Body).DoesNotContain("Frozen");
    }

    [Test]
    public async Task SearchTrack_Soulseek_IgnoresAnAlbumFoldersTitle_WhenPickingTheSong()
    {
        // A peer shares the whole "Invisible Touch" album, so every path contains the album name. The
        // search must pick the file that is actually the requested song, not an arbitrary album track
        // ("Land of Confusion") just because the folder matches. Choosing the wrong track fills the
        // library with unrelated songs and leaves the request missing.
        FakeHttpMessageHandler slskdHandler = new();
        slskdHandler.Fallback = (req, _) =>
        {
            string path = req.RequestUri!.AbsolutePath;
            string json = path.Contains("/session")
                ? """{ "token": "t" }"""
                : path.EndsWith("/server")
                    ? """{ "isLoggedIn": true }"""
                    : path.Contains("/searches")
                        ? """
                          { "id": "s1", "isComplete": true, "responses": [
                            { "username": "peerWrong", "hasFreeUploadSlot": true, "queueLength": 0, "files": [
                              { "filename": "Genesis - Invisible Touch (2007 Remaster)\\03 - Land of Confusion.flac", "size": 30000000, "bitRate": 0 } ] },
                            { "username": "peerRight", "hasFreeUploadSlot": true, "queueLength": 0, "files": [
                              { "filename": "Genesis - Invisible Touch (2007 Remaster)\\01 - Invisible Touch.flac", "size": 30000000, "bitRate": 0 } ] } ] }
                          """
                        : path.Contains("/transfers/downloads/")
                            ? """{ "ok": true }"""
                            : "[]";
            return Task.FromResult(FakeHttpMessageHandler.JsonResponse(json));
        };

        (MusicAcquisitionService acquisition, _, _, _) = Build(slskdEnabled: true, slskdHandler: slskdHandler);

        MusicGrabResult result = await acquisition.SearchTrackAndGrabAsync("Genesis", "Invisible Touch");

        await Assert.That(result.Success).IsTrue();
        List<CapturedRequest> enqueues = slskdHandler.Requests
            .Where(r => r.Method.Method == "POST" && r.Uri.AbsolutePath.Contains("/transfers/downloads/"))
            .ToList();
        await Assert.That(enqueues).HasCount(1);
        await Assert.That(enqueues[0].Body).Contains("Invisible Touch.flac");
        await Assert.That(enqueues[0].Body).DoesNotContain("Land of Confusion");
    }

    [Test]
    public async Task SearchTrack_Soulseek_DoesNotGrabAnAlbumTrackWhenOnlyTheFolderMatches()
    {
        // The only results are other songs inside the album folder named after the requested song.
        // None of them is the requested track, so the search must miss rather than download a random one.
        FakeHttpMessageHandler slskdHandler = new();
        slskdHandler.Fallback = (req, _) =>
        {
            string path = req.RequestUri!.AbsolutePath;
            string json = path.Contains("/session")
                ? """{ "token": "t" }"""
                : path.EndsWith("/server")
                    ? """{ "isLoggedIn": true }"""
                    : path.Contains("/searches")
                        ? """
                          { "id": "s1", "isComplete": true, "responses": [
                            { "username": "peer1", "hasFreeUploadSlot": true, "queueLength": 0, "files": [
                              { "filename": "Genesis - Invisible Touch (2007 Remaster)\\02 - Tonight, Tonight, Tonight.flac", "size": 30000000, "bitRate": 0 },
                              { "filename": "Genesis - Invisible Touch (2007 Remaster)\\04 - In Too Deep.flac", "size": 30000000, "bitRate": 0 } ] } ] }
                          """
                        : "[]";
            return Task.FromResult(FakeHttpMessageHandler.JsonResponse(json));
        };

        (MusicAcquisitionService acquisition, _, _, _) = Build(slskdEnabled: true, slskdHandler: slskdHandler);

        MusicGrabResult result = await acquisition.SearchTrackAndGrabAsync("Genesis", "Invisible Touch");

        await Assert.That(result.Success).IsFalse();
        await Assert.That(slskdHandler.Requests.Any(
            r => r.Method.Method == "POST" && r.Uri.AbsolutePath.Contains("/transfers/downloads/"))).IsFalse();
    }

    [Test]
    public async Task SearchTrack_Soulseek_RejectsASameTitledFileByAnotherArtist()
    {
        // A peer file named "Let It Go" that belongs to a different artist (Everlast) must not be
        // accepted for Idina Menzel's Frozen song.
        FakeHttpMessageHandler slskdHandler = new();
        slskdHandler.Fallback = (req, _) =>
        {
            string path = req.RequestUri!.AbsolutePath;
            string json = path.Contains("/session")
                ? """{ "token": "t" }"""
                : path.EndsWith("/server")
                    ? """{ "isLoggedIn": true }"""
                    : path.Contains("/searches")
                        ? """
                          { "id": "s1", "isComplete": true, "responses": [
                            { "username": "peer9", "hasFreeUploadSlot": true, "queueLength": 0, "files": [
                              { "filename": "Everlast - Love, War And The Ghost Of Whitey Ford\\16 - Let It Go.flac", "size": 30000000, "bitRate": 0 } ] } ] }
                          """
                        : "[]";
            return Task.FromResult(FakeHttpMessageHandler.JsonResponse(json));
        };

        (MusicAcquisitionService acquisition, _, _, _) = Build(slskdEnabled: true, slskdHandler: slskdHandler);

        MusicGrabResult result = await acquisition.SearchTrackAndGrabAsync(
            "Idina Menzel", "Let It Go (From \"Frozen\"/Soundtrack Version)");

        await Assert.That(result.Success).IsFalse();
    }

    [Test]
    public async Task SearchTrack_Soulseek_RejectsACoverOfTheSameSongByTheRightArtist()
    {
        // The artist matches, but a "(Cover)" is a version that was never requested.
        FakeHttpMessageHandler slskdHandler = new();
        slskdHandler.Fallback = (req, _) =>
        {
            string path = req.RequestUri!.AbsolutePath;
            string json = path.Contains("/session")
                ? """{ "token": "t" }"""
                : path.EndsWith("/server")
                    ? """{ "isLoggedIn": true }"""
                    : path.Contains("/searches")
                        ? """
                          { "id": "s1", "isComplete": true, "responses": [
                            { "username": "peer9", "hasFreeUploadSlot": true, "queueLength": 0, "files": [
                              { "filename": "Disney Covers\\Angela Lansbury - Beauty and the Beast (Cover).flac", "size": 30000000, "bitRate": 0 } ] } ] }
                          """
                        : "[]";
            return Task.FromResult(FakeHttpMessageHandler.JsonResponse(json));
        };

        (MusicAcquisitionService acquisition, _, _, _) = Build(slskdEnabled: true, slskdHandler: slskdHandler);

        MusicGrabResult result = await acquisition.SearchTrackAndGrabAsync("Angela Lansbury", "Beauty and the Beast");

        await Assert.That(result.Success).IsFalse();
    }

    [Test]
    public async Task SearchTrack_Soulseek_AllowsARemixWhenItWasRequested()
    {
        FakeHttpMessageHandler slskdHandler = new();
        slskdHandler.Fallback = (req, _) =>
        {
            string path = req.RequestUri!.AbsolutePath;
            string json = path.Contains("/session")
                ? """{ "token": "t" }"""
                : path.EndsWith("/server")
                    ? """{ "isLoggedIn": true }"""
                    : path.Contains("/searches")
                        ? """
                          { "id": "s1", "isComplete": true, "responses": [
                            { "username": "peer9", "hasFreeUploadSlot": true, "queueLength": 0, "files": [
                              { "filename": "Music\\Angela Lansbury - Beauty and the Beast (Remix).flac", "size": 30000000, "bitRate": 0 } ] } ] }
                          """
                        : path.Contains("/transfers/downloads/")
                            ? """{ "ok": true }"""
                            : "[]";
            return Task.FromResult(FakeHttpMessageHandler.JsonResponse(json));
        };

        (MusicAcquisitionService acquisition, _, _, _) = Build(slskdEnabled: true, slskdHandler: slskdHandler);

        MusicGrabResult result = await acquisition.SearchTrackAndGrabAsync("Angela Lansbury", "Beauty and the Beast (Remix)");

        await Assert.That(result.Success).IsTrue();
    }

    [Test]
    public async Task SearchTrack_Soulseek_SkipsABlacklistedSource_AndGrabsTheNext()
    {
        // The best-scoring peer already failed for this song: the search must move to the next good
        // candidate instead of giving up (which left songs stuck despite a search full of results).
        const string bad = "Music\\Idina Menzel\\01 - Let It Go.flac";
        const string good = "Music\\Idina Menzel\\Let It Go.mp3";
        FakeHttpMessageHandler slskdHandler = new();
        slskdHandler.Fallback = (req, _) =>
        {
            string path = req.RequestUri!.AbsolutePath;
            string json = path.Contains("/session")
                ? """{ "token": "t" }"""
                : path.EndsWith("/server")
                    ? """{ "isLoggedIn": true }"""
                    : path.Contains("/searches")
                        ? $$"""
                          { "id": "s1", "isComplete": true, "responses": [
                            { "username": "peerA", "hasFreeUploadSlot": true, "queueLength": 0, "files": [
                              { "filename": {{System.Text.Json.JsonSerializer.Serialize(bad)}}, "size": 30000000, "bitRate": 0 } ] },
                            { "username": "peerB", "hasFreeUploadSlot": false, "queueLength": 0, "files": [
                              { "filename": {{System.Text.Json.JsonSerializer.Serialize(good)}}, "size": 8000000, "bitRate": 320 } ] } ] }
                          """
                        : path.Contains("/transfers/downloads/")
                            ? """{ "ok": true }"""
                            : "[]";
            return Task.FromResult(FakeHttpMessageHandler.JsonResponse(json));
        };

        (MusicAcquisitionService acquisition, _, _, TestMusicDb db) = Build(slskdEnabled: true, slskdHandler: slskdHandler);
        db.Seed(d => d.Set<GrabRecord>().Add(new GrabRecord
        {
            AlbumId = 0,
            Source = "soulseek",
            Identifier = MusicAcquisitionService.SoulseekFileKey(bad),
            ReleaseTitle = "Let It Go",
            State = "Failed",
            DeezerTrackId = 72371930,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        }));

        MusicGrabResult result = await acquisition.SearchTrackAndGrabAsync("Idina Menzel", "Let It Go", 72371930);

        await Assert.That(result.Success).IsTrue();
        using ArrkDbContext ctx = db.CreateDbContext();
        GrabRecord grabbed = ctx.Set<GrabRecord>().Single(g => g.Source == "soulseek" && g.State == "Grabbed");
        await Assert.That(grabbed.Identifier).IsEqualTo(MusicAcquisitionService.SoulseekFileKey(good));
    }

    [Test]
    public async Task StreamReleasesAsync_YieldsMatches()
    {
        (MusicAcquisitionService acquisition, _, _, _) = Build(
            prowlarrResponder: _ => SearchJson(
                Release("Ella Langley - Dandelion (2026) [Flac 24-48]", seeders: 75)));

        List<MusicRelease> got = [];
        await foreach (MusicRelease release in acquisition.StreamReleasesAsync("Ella Langley", "Dandelion", requireArtist: false))
            got.Add(release);

        await Assert.That(got).IsNotEmpty();
    }

    [Test]
    public async Task ChooseAlbumFolder_PicksTheMatchingAlbum_FromACompilation()
    {
        string root = Path.Combine(Path.GetTempPath(), "stackarr-cmp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Off The Wall (1979) (2014 PBTHAL Vinyl) [FLAC]"));
        Directory.CreateDirectory(Path.Combine(root, "Thriller (1982) (2014 PBTHAL Vinyl) [FLAC]"));
        try
        {
            string picked = MusicAcquisitionService.ChooseAlbumFolder(root, "Thriller");

            await Assert.That(Path.GetFileName(picked)).Contains("Thriller");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task ChooseAlbumFolder_ReturnsRoot_ForASingleAlbum()
    {
        string root = Path.Combine(Path.GetTempPath(), "stackarr-one-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Disc 1"));
        try
        {
            string picked = MusicAcquisitionService.ChooseAlbumFolder(root, "Some Album");
            await Assert.That(picked).IsEqualTo(root);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task PlaceAlbum_ForSingleFile_PlacesItCanonically()
    {
        string root = Path.Combine(Path.GetTempPath(), "stackarr-place-" + Guid.NewGuid().ToString("N"));
        string downloads = Path.Combine(root, "downloads");
        Directory.CreateDirectory(downloads);
        await File.WriteAllTextAsync(Path.Combine(downloads, "Other Album Track.mp3"), "x");
        string single = Path.Combine(downloads, "Mr. Brightside.mp3");
        await File.WriteAllTextAsync(single, "y");
        string library = Path.Combine(root, "library");

        (MusicAcquisitionService acquisition, _, _, _) = Build();
        acquisition.LibraryRoot = library;

        Album album = new()
        {
            Id = 1,
            ArtistName = "The Killers",
            Title = "Hot Fuss",
            DeezerId = 123,
            Tracks =
            [
                new Track { Id = 1, Title = "Mr. Brightside", TrackNumber = 1, AbsoluteTrackNumber = 1, MediumNumber = 1, DeezerId = 55 }
            ]
        };

        try
        {
            // A single-file source is placed under the album's canonical Artist/Album folder, named
            // from the track metadata, not the download's own name.
            List<(int TrackId, string FilePath)> placed = await acquisition.PlaceAlbumAsync(album, single, null, move: false, CancellationToken.None);

            await Assert.That(placed).HasCount(1);
            await Assert.That(File.Exists(Path.Combine(library, "The Killers", "Hot Fuss", "01 - Mr. Brightside.mp3"))).IsTrue();
            await Assert.That(File.Exists(Path.Combine(library, "The Killers", "Hot Fuss", "Other Album Track.mp3"))).IsFalse();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task PlaceAlbum_ForASingleRequestedTrack_RefusesToPlaceItOnADifferentTrack()
    {
        // "Invisible Touch" carries both the title track and "In Too Deep". A grab that recorded the
        // Deezer id of a track this release does not hold (999) must not fall back to a title match
        // and place the file under "In Too Deep". A requested song binds to its exact track.
        string root = Path.Combine(Path.GetTempPath(), "stackarr-place-" + Guid.NewGuid().ToString("N"));
        string downloads = Path.Combine(root, "downloads");
        Directory.CreateDirectory(downloads);
        string single = Path.Combine(downloads, "In Too Deep.flac");
        await File.WriteAllTextAsync(single, "x");
        string library = Path.Combine(root, "library");

        (MusicAcquisitionService acquisition, _, _, _) = Build();
        acquisition.LibraryRoot = library;

        Album album = new()
        {
            Id = 1,
            ArtistName = "Genesis",
            Title = "Invisible Touch",
            DeezerId = 123,
            Tracks =
            [
                new Track { Id = 1, Title = "Invisible Touch", TrackNumber = 1, AbsoluteTrackNumber = 1, MediumNumber = 1, DeezerId = 111 },
                new Track { Id = 2, Title = "In Too Deep", TrackNumber = 2, AbsoluteTrackNumber = 2, MediumNumber = 1, DeezerId = 222 }
            ]
        };

        try
        {
            // The grab names a track this release doesn't hold: nothing is placed.
            List<(int TrackId, string FilePath)> rejected = await acquisition.PlaceAlbumAsync(
                album, single, null, move: false, CancellationToken.None, requestedTrackDeezerId: 999);
            await Assert.That(rejected).IsEmpty();
            await Assert.That(File.Exists(Path.Combine(library, "Genesis", "Invisible Touch", "02 - In Too Deep.flac"))).IsFalse();

            // The real requested track still binds exactly.
            List<(int TrackId, string FilePath)> placed = await acquisition.PlaceAlbumAsync(
                album, single, null, move: false, CancellationToken.None, requestedTrackDeezerId: 222);
            await Assert.That(placed).HasCount(1);
            await Assert.That(placed[0].TrackId).IsEqualTo(2);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task PlaceAlbum_NamesMultiDiscFilesWithTheDiscNumber()
    {
        string root = Path.Combine(Path.GetTempPath(), "stackarr-disc-" + Guid.NewGuid().ToString("N"));
        string downloads = Path.Combine(root, "downloads");
        Directory.CreateDirectory(downloads);
        await File.WriteAllTextAsync(Path.Combine(downloads, "Disc 2 - Track.flac"), "a");
        string library = Path.Combine(root, "library");

        (MusicAcquisitionService acquisition, _, _, _) = Build();
        acquisition.LibraryRoot = library;

        Album album = new()
        {
            Id = 1,
            ArtistName = "The Wall",
            Title = "Live",
            DeezerId = 9,
            Tracks =
            [
                new Track { Id = 1, Title = "In the Flesh", TrackNumber = 1, AbsoluteTrackNumber = 1, MediumNumber = 1, DeezerId = 2 },
                new Track { Id = 2, Title = "Hey You", TrackNumber = 2, AbsoluteTrackNumber = 2, MediumNumber = 2, DeezerId = 1 }
            ]
        };

        try
        {
            List<(int TrackId, string FilePath)> placed = await acquisition.PlaceAlbumAsync(album, downloads, null, move: false, CancellationToken.None);

            await Assert.That(placed).HasCount(1);
            await Assert.That(File.Exists(Path.Combine(library, "The Wall", "Live", "2-02 - Hey You.flac"))).IsTrue();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // Single → containing album fallback

    [Test]
    public async Task Search_SingleNotOnIndexers_FallsBackToContainingAlbum()
    {
        (MusicAcquisitionService acquisition, _, _, TestMusicDb db) = Build(
            prowlarrResponder: req =>
            {
                string query = Uri.UnescapeDataString(req.RequestUri!.Query).ToLowerInvariant();
                return query.Contains("dandelion")
                    ? SearchJson(Release("Ella Langley - Dandelion (2026 Blues Country Folk) [Flac 24-48]", seeders: 75))
                    : "[]";
            });

        db.Seed(d =>
        {
            MusicArtist artist = new() { Name = "Ella Langley", SortName = "Langley, Ella", Added = DateTime.UtcNow };
            artist.Albums.Add(new MusicAlbum
            {
                Title = "Choosin’ Texas",
                Monitored = true,
                TotalTrackCount = 1,
                Tracks = { new MusicTrack { Title = "Choosin’ Texas", TrackNumber = 1, AbsoluteTrackNumber = 1 } }
            });
            artist.Albums.Add(new MusicAlbum
            {
                Title = "Dandelion",
                Monitored = true,
                TotalTrackCount = 19,
                Tracks =
                {
                    new MusicTrack { Title = "Dandelion", TrackNumber = 1, AbsoluteTrackNumber = 1 },
                    new MusicTrack { Title = "Choosin’ Texas", TrackNumber = 2, AbsoluteTrackNumber = 2 }
                }
            });
            d.Set<MusicArtist>().Add(artist);
        });

        List<MusicRelease> releases = await acquisition.SearchReleasesAsync("Ella Langley", "Choosin’ Texas");

        await Assert.That(releases).IsNotEmpty();
        await Assert.That(releases[0].Title).Contains("Dandelion");
        await Assert.That(releases[0].MatchedTarget).IsEqualTo("Dandelion");
    }

    [Test]
    public async Task Search_PrefersReleasesWithMagnet_OverProxyOnlyLinks()
    {
        // 1337x exposes only a Prowlarr proxy link (no magnet) that Transmission can't fetch; the
        // torrent release with a magnet must rank above it even if its title match is no better.
        (MusicAcquisitionService acquisition, _, _, _) = Build(
            prowlarrResponder: _ => SearchJson(
                Release("Ed Sheeran   ÷ (Deluxe) (2017) FLAC", seeders: 5, guid: "https://1337x.st/torrent/1/x"),
                Release("Ed Sheeran - ÷ (Deluxe) (2017) FLAC", seeders: 2)));

        List<MusicRelease> releases = await acquisition.SearchReleasesAsync("Ed Sheeran", "÷ (Deluxe)");

        await Assert.That(releases).IsNotEmpty();
        await Assert.That(releases[0].HasMagnet).IsTrue();
        await Assert.That(releases[0].Indexer).IsEqualTo("The Pirate Bay");
    }

    // Grab via Transmission

    [Test]
    public async Task Grab_AddsTorrentWithLabel_AndStagingPath()
    {
        List<(string Method, string Body)> calls = [];
        (MusicAcquisitionService acquisition, _, FakeHttpMessageHandler transmission, _) = Build(
            transmissionResponder: req =>
            {
                string body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                using JsonDocument doc = JsonDocument.Parse(body);
                string method = doc.RootElement.GetProperty("method").GetString()!;
                calls.Add((method, body));
                return method switch
                {
                    "torrent-add" => TransmissionOk("""{ "torrent-added": { "id": 42, "hashString": "abc" } }"""),
                    "torrent-get" => TransmissionOk("""{ "torrents": [] }"""),
                    _ => TransmissionOk()
                };
            });

        MusicRelease release = new()
        {
            Title = "Ella Langley - Dandelion (2026) [Flac 24-48]",
            MagnetUrl = "magnet:?xt=urn:btih:ABC123",
            InfoHash = "abc123",
            Indexer = "The Pirate Bay",
            Seeders = 75
        };

        MusicGrabResult result = await acquisition.GrabAsync(release);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.TorrentId).IsEqualTo(42);
        (string method, string body) add = calls.Single(c => c.Method == "torrent-add");
        await Assert.That(add.body).Contains("[\"music\"]");
        await Assert.That(add.body).Contains(MusicAcquisitionService.MusicDownloadPath);
        await Assert.That(transmission.Requests.Any(r => r.Uri.AbsolutePath.Contains("transmission/rpc"))).IsTrue();
    }

    [Test]
    public async Task Grab_AlreadyDownloading_IsNotDuplicated()
    {
        bool hasTorrent = false;
        List<string> addCalls = [];
        (MusicAcquisitionService acquisition, _, _, _) = Build(
            transmissionResponder: req =>
            {
                string body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                using JsonDocument doc = JsonDocument.Parse(body);
                string method = doc.RootElement.GetProperty("method").GetString()!;
                if (method == "torrent-add") addCalls.Add(body);
                return method switch
                {
                    "torrent-get" => hasTorrent
                        ? TransmissionOk("""{ "torrents": [ { "id": 5, "hashString": "ABC123", "percentDone": 0.4, "labels": ["music"] } ] }""")
                        : TransmissionOk("""{ "torrents": [] }"""),
                    _ => TransmissionOk()
                };
            });

        MusicRelease release = new() { Title = "X", MagnetUrl = "magnet:?xt=urn:btih:ABC123", InfoHash = "ABC123" };

        MusicGrabResult first = await acquisition.GrabAsync(release);
        hasTorrent = true;
        MusicGrabResult second = await acquisition.GrabAsync(release);

        await Assert.That(first.Success).IsTrue();
        await Assert.That(second.Success).IsTrue();
        await Assert.That(second.Message).Contains("Already");
        await Assert.That(addCalls.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Grab_RecordsGrabKeyedByLowercaseInfohash()
    {
        (MusicAcquisitionService acquisition, _, _, TestMusicDb db) = Build(
            transmissionResponder: req =>
            {
                using JsonDocument doc = JsonDocument.Parse(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                return doc.RootElement.GetProperty("method").GetString() switch
                {
                    "torrent-add" => TransmissionOk("""{ "torrent-added": { "id": 7, "hashString": "abc" } }"""),
                    _ => TransmissionOk("""{ "torrents": [] }""")
                };
            });

        MusicRelease release = new()
        {
            Title = "X",
            MagnetUrl = "magnet:?xt=urn:btih:ABCDEF",
            InfoHash = "ABCDEF",
            Indexer = "TPB"
        };
        await acquisition.GrabAsync(release, albumId: 42);

        using ArrkDbContext context = db.CreateDbContext();
        GrabRecord? record = context.Set<GrabRecord>().FirstOrDefault(g => g.AlbumId == 42);
        await Assert.That(record).IsNotNull();
        await Assert.That(record!.Identifier).IsEqualTo("abcdef");
    }

    // Import

    [Test]
    public async Task ImportCompleted_PlacesFilesInLibrary_AndRelabels()
    {
        string root = Path.Combine(Path.GetTempPath(), "stackarr-test-" + Guid.NewGuid().ToString("N"));
        string download = Path.Combine(root, "download", "Ella Langley - Dandelion");
        Directory.CreateDirectory(Path.Combine(download, "Disc 1"));
        await File.WriteAllTextAsync(Path.Combine(download, "Disc 1", "01 Dandelion.flac"), "audio");
        string library = Path.Combine(root, "library");

        List<(string Method, string Body)> transmissionCalls = [];
        (MusicAcquisitionService acquisition, _, _, _) = Build(
            deezerHandler: DeezerAlbum(123, "Dandelion", "Ella Langley", ("Dandelion", 1)),
            transmissionResponder: req =>
            {
                string body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                using JsonDocument doc = JsonDocument.Parse(body);
                string method = doc.RootElement.GetProperty("method").GetString()!;
                transmissionCalls.Add((method, body));
                return method switch
                {
                    "torrent-get" => TransmissionOk($$"""
                        { "torrents": [ { "id": 5, "name": "Ella Langley - Dandelion", "hashString": "H",
                          "percentDone": 1.0, "labels": ["music"],
                          "downloadDir": {{JsonSerializer.Serialize(Path.Combine(root, "download"))}} } ] }
                        """),
                    _ => TransmissionOk()
                };
            });
        acquisition.LibraryRoot = library;

        try
        {
            int imported = await acquisition.ImportCompletedAsync();

            await Assert.That(imported).IsEqualTo(1);
            // Placed into the album's canonical layout, not the torrent's own folder name.
            await Assert.That(File.Exists(Path.Combine(library, "Ella Langley", "Dandelion", "01 - Dandelion.flac"))).IsTrue();
            await Assert.That(transmissionCalls.Any(c => c.Method == "torrent-set" && c.Body.Contains("music-imported"))).IsTrue();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task ImportCompleted_IsIdempotent_WhenFilesAlreadyPlaced()
    {
        string root = Path.Combine(Path.GetTempPath(), "stackarr-test-" + Guid.NewGuid().ToString("N"));
        string download = Path.Combine(root, "download", "Halo 3 OST");
        Directory.CreateDirectory(download);
        await File.WriteAllTextAsync(Path.Combine(download, "01 Track.mp3"), "audio");
        string library = Path.Combine(root, "library");
        Directory.CreateDirectory(Path.Combine(library, "Halo 3 OST"));
        await File.WriteAllTextAsync(Path.Combine(library, "Halo 3 OST", "01 Track.mp3"), "already");

        (MusicAcquisitionService acquisition, _, _, _) = Build(
            transmissionResponder: req =>
            {
                string body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                using JsonDocument doc = JsonDocument.Parse(body);
                return doc.RootElement.GetProperty("method").GetString() switch
                {
                    "torrent-get" => TransmissionOk($$"""
                        { "torrents": [ { "id": 5, "name": "Halo 3 OST", "hashString": "H",
                          "percentDone": 1.0, "labels": ["music"],
                          "downloadDir": {{JsonSerializer.Serialize(Path.Combine(root, "download"))}} } ] }
                        """),
                    _ => TransmissionOk()
                };
            });
        acquisition.LibraryRoot = library;

        try
        {
            int imported = await acquisition.ImportCompletedAsync();
            await Assert.That(imported).IsEqualTo(0);
            await Assert.That(await File.ReadAllTextAsync(Path.Combine(library, "Halo 3 OST", "01 Track.mp3"))).IsEqualTo("already");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task ImportCompleted_IgnoresIncompleteAndForeignTorrents()
    {
        (MusicAcquisitionService acquisition, _, _, _) = Build(
            transmissionResponder: req =>
            {
                string body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                using JsonDocument doc = JsonDocument.Parse(body);
                return doc.RootElement.GetProperty("method").GetString() switch
                {
                    "torrent-get" => TransmissionOk("""
                        { "torrents": [
                            { "id": 1, "name": "incomplete", "hashString": "a", "percentDone": 0.5, "labels": ["music"] },
                            { "id": 2, "name": "other", "hashString": "b", "percentDone": 1.0, "labels": ["movies"] }
                        ] }
                        """),
                    _ => TransmissionOk()
                };
            });

        int imported = await acquisition.ImportCompletedAsync();

        await Assert.That(imported).IsEqualTo(0);
    }

    [Test]
    public async Task Search_DeprioritisesCompanionAlbum_ThatOnlyAppendsATitleWord()
    {
        // A release for a different album in the same series (Halo 3: ODST for Halo 3) shares every
        // word of the request. A name is only a hint, so a companion is scored below the real album
        // and normal editions rather than dropped; dropping it would starve an album whose only
        // torrent carries a bare edition word (see Search_KeepsBareEditionRelease...).
        (MusicAcquisitionService acquisition, _, _, _) = Build(
            prowlarrResponder: _ => SearchJson(
                Release("Halo 3 - ODST Soundtrack [FLAC]", seeders: 20),
                Release("Halo 3 ODST Original Soundtrack [Flac 24-48]", seeders: 15),
                Release("Halo 3 OST (Original Soundtrack)", seeders: 1),
                Release("Halo 3 (Deluxe Edition) [FLAC]", seeders: 1)));

        List<MusicRelease> releases = await acquisition.SearchReleasesAsync("Halo", "Halo 3 (Original Soundtrack)");

        // The companion is still returned, so the album is never starved when it is the only option…
        await Assert.That(releases.Any(r => r.Title.Contains("ODST", StringComparison.OrdinalIgnoreCase))).IsTrue();
        // …but it ranks below every release that really is the album, so a grab never picks it first.
        await Assert.That(releases[0].Title.Contains("ODST", StringComparison.OrdinalIgnoreCase)).IsFalse();
        MusicRelease? companion = releases.FirstOrDefault(r => r.Title.Contains("ODST", StringComparison.OrdinalIgnoreCase));
        MusicRelease? real = releases.FirstOrDefault(r => r.Title.Contains("Halo 3 OST", StringComparison.OrdinalIgnoreCase));
        await Assert.That(companion).IsNotNull();
        await Assert.That(real).IsNotNull();
        await Assert.That(companion!.Score).IsLessThan(real!.Score);
        await Assert.That(releases.Any(r => r.Title.Contains("Deluxe", StringComparison.OrdinalIgnoreCase))).IsTrue();
    }

    [Test]
    public async Task Search_KeepsBareEditionRelease_WhenItIsTheOnlyMatch()
    {
        // A release whose title appends a bare edition word the noise list doesn't know ("20th
        // Anniversary Edition") must still match: a name-based hard veto leaves the album with nothing,
        // even though its files really are the album. The importer's file/track check, not the title
        // string, is what refuses a genuinely different album.
        (MusicAcquisitionService acquisition, _, _, _) = Build(
            prowlarrResponder: _ => SearchJson(
                Release("Nirvana - Nevermind 20th Anniversary Edition [FLAC]", seeders: 3)));

        List<MusicRelease> releases = await acquisition.SearchReleasesAsync("Nirvana", "Nevermind");

        await Assert.That(releases).IsNotEmpty();
        await Assert.That(releases[0].Title).Contains("Anniversary");
    }

    [Test]
    public async Task ReleaseMatchesAlbum_RejectsDifferentRelease_ByTrackTitles()
    {
        Album halo3 = new()
        {
            Id = 1,
            Title = "Halo 3 (Original Soundtrack)",
            ArtistName = "Halo",
            Tracks =
            [
                new Track { Id = 1, Title = "Luck" },
                new Track { Id = 2, Title = "Released" }
            ]
        };

        // "Halo 3: ODST" files carry real titles that share nothing with Halo 3's tracks.
        await Assert.That(MusicAcquisitionService.ReleaseMatchesAlbum(halo3,
            ["/x/01 - Overture.flac", "/x/02 - The Rookie.flac"])).IsFalse();
        // The right release (or even one overlapping word) is accepted.
        await Assert.That(MusicAcquisitionService.ReleaseMatchesAlbum(halo3,
            ["/x/01 - Luck.flac", "/x/02 - Released.flac"])).IsTrue();
        // A number-only rip has no title to judge, so positional placement stands.
        await Assert.That(MusicAcquisitionService.ReleaseMatchesAlbum(halo3,
            ["/x/01.flac", "/x/02.flac"])).IsTrue();
    }

    [Test]
    public async Task Import_DoesNotPlaceADifferentRelease_UnderTheRequestedAlbum()
    {
        string root = Path.Combine(Path.GetTempPath(), "stackarr-mismatch-" + Guid.NewGuid().ToString("N"));
        string download = Path.Combine(root, "download");
        string torrentDir = Path.Combine(download, "Halo 3 - ODST Soundtrack");
        Directory.CreateDirectory(torrentDir);
        await File.WriteAllTextAsync(Path.Combine(torrentDir, "01 - Overture.flac"), "audio");
        await File.WriteAllTextAsync(Path.Combine(torrentDir, "02 - The Rookie.flac"), "audio");
        string library = Path.Combine(root, "library");

        List<(string Method, string Body)> calls = [];
        (MusicAcquisitionService acquisition, _, _, TestMusicDb db) = Build(
            transmissionResponder: req =>
            {
                string body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                using JsonDocument doc = JsonDocument.Parse(body);
                string method = doc.RootElement.GetProperty("method").GetString()!;
                calls.Add((method, body));
                return method switch
                {
                    "torrent-get" => TransmissionOk($$"""
                        { "torrents": [ { "id": 5, "name": "Halo 3 - ODST Soundtrack", "hashString": "h",
                          "percentDone": 1.0, "labels": ["music"],
                          "downloadDir": {{JsonSerializer.Serialize(download)}} } ] }
                        """),
                    _ => TransmissionOk()
                };
            });
        acquisition.LibraryRoot = library;

        int albumId = 0;
        db.Seed(d =>
        {
            MusicArtist artist = new() { Name = "Halo", Added = DateTime.UtcNow };
            MusicAlbum album = new() { Title = "Halo 3 (Original Soundtrack)", DeezerId = 430144257, TotalTrackCount = 2, Added = DateTime.UtcNow };
            album.Tracks.Add(new MusicTrack { Title = "Luck", TrackNumber = 1, AbsoluteTrackNumber = 1 });
            album.Tracks.Add(new MusicTrack { Title = "Released", TrackNumber = 2, AbsoluteTrackNumber = 2 });
            artist.Albums.Add(album);
            d.Set<MusicArtist>().Add(artist);
            d.SaveChanges();
            albumId = album.Id;
            d.Set<GrabRecord>().Add(new GrabRecord
            {
                AlbumId = album.Id,
                Source = "torrent",
                Identifier = "h",
                ReleaseTitle = "Halo 3 - ODST Soundtrack",
                State = "Grabbed",
                DeezerAlbumId = 430144257,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        });

        try
        {
            int imported = await acquisition.ImportCompletedAsync();

            await Assert.That(imported).IsEqualTo(0);
            // The release is relabelled as a mismatch so it stops being retried, and kept for its real album.
            await Assert.That(calls.Any(c => c.Method == "torrent-set" && c.Body.Contains("music-mismatch"))).IsTrue();
            await Assert.That(Directory.Exists(Path.Combine(library, "Halo", "Halo 3 (Original Soundtrack)"))).IsFalse();

            using ArrkDbContext ctx = db.CreateDbContext();
            await Assert.That(ctx.Set<MusicTrack>().Any(t => t.HasFile)).IsFalse();
            await Assert.That(ctx.Set<GrabRecord>().Single(g => g.Identifier == "h").State).IsEqualTo("Failed");
            // The requested album was re-queued so another release is searched.
            await Assert.That(ctx.Set<MusicAcquireQueueItem>().Any(q => q.AlbumId == albumId)).IsTrue();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Grab_ReusesACompletedTorrent_ForTheAlbumItActuallyIs()
    {
        string root = Path.Combine(Path.GetTempPath(), "stackarr-reuse-" + Guid.NewGuid().ToString("N"));
        string download = Path.Combine(root, "download");
        string torrentDir = Path.Combine(download, "Halo 3 - ODST Soundtrack");
        Directory.CreateDirectory(torrentDir);
        await File.WriteAllTextAsync(Path.Combine(torrentDir, "01 - Overture.flac"), "audio");
        await File.WriteAllTextAsync(Path.Combine(torrentDir, "02 - The Rookie.flac"), "audio");
        string library = Path.Combine(root, "library");
        string wrongOne = "Halo/Halo 3 (Original Soundtrack)/01 - Luck.flac";
        string wrongTwo = "Halo/Halo 3 (Original Soundtrack)/02 - Released.flac";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(library, wrongOne))!);
        await File.WriteAllTextAsync(Path.Combine(library, wrongOne), "wrong");
        await File.WriteAllTextAsync(Path.Combine(library, wrongTwo), "wrong");

        List<(string Method, string Body)> calls = [];
        (MusicAcquisitionService acquisition, _, _, TestMusicDb db) = Build(
            transmissionResponder: req =>
            {
                string body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                using JsonDocument doc = JsonDocument.Parse(body);
                string method = doc.RootElement.GetProperty("method").GetString()!;
                calls.Add((method, body));
                return method switch
                {
                    "torrent-get" => TransmissionOk($$"""
                        { "torrents": [ { "id": 5, "name": "Halo 3 - ODST Soundtrack", "hashString": "h",
                          "percentDone": 1.0, "labels": ["music-imported"],
                          "downloadDir": {{JsonSerializer.Serialize(download)}} } ] }
                        """),
                    _ => TransmissionOk()
                };
            });
        acquisition.LibraryRoot = library;

        int odstId = 0;
        db.Seed(d =>
        {
            MusicArtist artist = new() { Name = "Halo", Added = DateTime.UtcNow };
            MusicAlbum wrong = new() { Title = "Halo 3 (Original Soundtrack)", TotalTrackCount = 2, Added = DateTime.UtcNow };
            wrong.Tracks.Add(new MusicTrack { Title = "Luck", TrackNumber = 1, AbsoluteTrackNumber = 1, HasFile = true, FilePath = wrongOne });
            wrong.Tracks.Add(new MusicTrack { Title = "Released", TrackNumber = 2, AbsoluteTrackNumber = 2, HasFile = true, FilePath = wrongTwo });
            MusicAlbum odst = new() { Title = "Halo 3: ODST (Original Soundtrack)", TotalTrackCount = 2, Added = DateTime.UtcNow };
            odst.Tracks.Add(new MusicTrack { Title = "Overture", TrackNumber = 1, AbsoluteTrackNumber = 1 });
            odst.Tracks.Add(new MusicTrack { Title = "The Rookie", TrackNumber = 2, AbsoluteTrackNumber = 2 });
            artist.Albums.Add(wrong);
            artist.Albums.Add(odst);
            d.Set<MusicArtist>().Add(artist);
            d.SaveChanges();
            odstId = odst.Id;
            d.Set<GrabRecord>().Add(new GrabRecord
            {
                AlbumId = wrong.Id,
                Source = "torrent",
                Identifier = "h",
                ReleaseTitle = "Halo 3 - ODST Soundtrack",
                State = "Imported",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        });

        MusicRelease release = new()
        {
            Title = "Halo 3: ODST (Original Soundtrack)",
            MagnetUrl = "magnet:?xt=urn:btih:H",
            InfoHash = "H",
            Indexer = "TPB"
        };

        try
        {
            MusicGrabResult result = await acquisition.GrabAsync(release, albumId: odstId);

            await Assert.That(result.Success).IsTrue();
            await Assert.That(result.Message).Contains("Reusing");
            // The wrong album's files are cleared and the torrent is re-queued for import under ODST.
            await Assert.That(File.Exists(Path.Combine(library, wrongOne))).IsFalse();
            await Assert.That(File.Exists(Path.Combine(library, wrongTwo))).IsFalse();
            await Assert.That(calls.Any(c => c.Method == "torrent-set" && c.Body.Contains("[\"music\"]"))).IsTrue();

            using ArrkDbContext ctx = db.CreateDbContext();
            GrabRecord grab = ctx.Set<GrabRecord>().Single(g => g.Identifier == "h");
            await Assert.That(grab.AlbumId).IsEqualTo(odstId);
            await Assert.That(grab.State).IsEqualTo("Grabbed");
            await Assert.That(ctx.Set<MusicTrack>().Any(t => t.HasFile)).IsFalse();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // Auto-resolve after a failed download

    [Test]
    public async Task ReacquireAlbum_ExcludesFailedRelease_AndGrabsNextBest()
    {
        List<string> added = [];
        (MusicAcquisitionService acquisition, _, _, TestMusicDb db) = Build(
            prowlarrResponder: req => req.RequestUri!.AbsolutePath.Contains("search")
                ? SearchJson(
                    Release("Halo 3 - ODST - OST (Kingdom Music) 2009", seeders: 5, infoHash: "AAAA000000000000000000000000000000000000"),
                    Release("Halo 3 ODST Original Soundtrack [Flac 24-48]", seeders: 4, infoHash: "BBBB111111111111111111111111111111111111"))
                : "[]",
            transmissionResponder: req =>
            {
                string body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                using JsonDocument doc = JsonDocument.Parse(body);
                string method = doc.RootElement.GetProperty("method").GetString()!;
                if (method == "torrent-add")
                {
                    added.Add(body);
                    return TransmissionOk("""{ "torrent-added": { "id": 77, "hashString": "bbbb" } }""");
                }
                return TransmissionOk("""{ "torrents": [] }""");
            });

        int albumId = 0;
        db.Seed(d =>
        {
            MusicArtist artist = new() { Name = "Martin O’Donnell", Added = DateTime.UtcNow };
            MusicAlbum album = new() { Title = "Halo 3: ODST: Original Soundtrack", Monitored = true };
            artist.Albums.Add(album);
            d.Set<MusicArtist>().Add(artist);
            d.SaveChanges();
            albumId = album.Id;
        });

        MusicGrabResult result = await acquisition.ReacquireAlbumAsync(
            albumId, "Halo 3 - ODST - OST (Kingdom Music) 2009");

        await Assert.That(result.Success).IsTrue();
        await Assert.That(added).HasCount(1);
        await Assert.That(added[0]).Contains("BBBB111111111111111111111111111111111111");
        await Assert.That(added[0]).DoesNotContain("AAAA000000000000000000000000000000000000");
    }

    [Test]
    public async Task ReacquireAlbum_UnknownAlbum_ReturnsFailure()
    {
        (MusicAcquisitionService acquisition, _, _, _) = Build();

        MusicGrabResult result = await acquisition.ReacquireAlbumAsync(0, "whatever");

        await Assert.That(result.Success).IsFalse();
    }

    [Test]
    public async Task CleanTrackTitle_StripsDeezerDisambiguation()
    {
        await Assert.That(MusicText.CleanTrackTitle("Let It Go (From \"Frozen\"/Soundtrack Version)")).IsEqualTo("Let It Go");
        await Assert.That(MusicText.CleanTrackTitle("Song (Remastered 2011)")).IsEqualTo("Song");
        // A title that is only a year must survive (the cleaner would otherwise empty it).
        await Assert.That(MusicText.CleanTrackTitle("1999")).IsEqualTo("1999");
    }

    [Test]
    public async Task SoulseekFileKey_DistinguishesFilesInAGenericFolder()
    {
        // A generic peer folder ("music") collides on the folder key but not on the file key.
        await Assert.That(MusicAcquisitionService.SoulseekGrabKey(@"PeerA\music\Song A.flac"))
            .IsEqualTo(MusicAcquisitionService.SoulseekGrabKey(@"PeerB\music\Song B.flac"));
        await Assert.That(MusicAcquisitionService.SoulseekFileKey(@"PeerA\music\Song A.flac"))
            .IsNotEqualTo(MusicAcquisitionService.SoulseekFileKey(@"PeerB\music\Song B.flac"));
    }

    [Test]
    public async Task MatchTrackByTitle_IgnoresTheDisambiguationTail()
    {
        List<Track> tracks =
        [
            new() { Id = 1, Title = "Let It Go (From \"Frozen\"/Soundtrack Version)", TrackNumber = 1, AbsoluteTrackNumber = 1, MediumNumber = 1 },
            new() { Id = 22, Title = "We Were So Close (From \"Frozen\"/Score)", TrackNumber = 22, AbsoluteTrackNumber = 22, MediumNumber = 1 }
        ];

        // A "1-22 Artist - Let It Go" file must match the song, not the score piece that happens to be
        // track 22 on the album.
        Track? match = MusicAcquisitionService.MatchTrackByTitle(tracks, [], "1-22 Idina Menzel - Let It Go");

        await Assert.That(match).IsNotNull();
        await Assert.That(match!.Id).IsEqualTo(1);
    }

    [Test]
    public async Task SearchAndGrab_TorrentsDisabled_NeverHitsProwlarr()
    {
        (MusicAcquisitionService acquisition, FakeHttpMessageHandler prowlarr, _, _) =
            Build(configuration: SourcesConfig(torrents: false, soulseek: false));

        MusicGrabResult result = await acquisition.SearchAndGrabAsync("Artist", "Album", null, requireArtist: false);

        await Assert.That(result.Success).IsFalse();
        await Assert.That(prowlarr.Requests).IsEmpty();
    }

    [Test]
    public async Task SearchAndGrab_SoulseekDisabled_DoesNotTouchSlskd()
    {
        FakeHttpMessageHandler slskd = new();
        (MusicAcquisitionService acquisition, _, _, _) = Build(
            slskdEnabled: true,
            slskdHandler: slskd,
            configuration: SourcesConfig(torrents: true, soulseek: false));

        // No Prowlarr results configured, so this returns failure without ever calling slskd.
        MusicGrabResult result = await acquisition.SearchTrackAndGrabAsync("Artist", "Song");

        await Assert.That(result.Success).IsFalse();
        await Assert.That(slskd.Requests).IsEmpty();
    }

    [Test]
    public async Task TrackSearch_WithSoulseekEnabled_NeverUsesTorrents()
    {
        // A single song is Soulseek-only: the indexer must not be searched for it.
        (MusicAcquisitionService acquisition, FakeHttpMessageHandler prowlarr, _, _) =
            Build(slskdEnabled: true, slskdHandler: MusicTestHelpers.EmptyHandler(), prowlarrResponder: _ => "[]");

        MusicGrabResult result = await acquisition.SearchTrackAndGrabAsync("Idina Menzel", "Let It Go");

        await Assert.That(result.Success).IsFalse();
        await Assert.That(prowlarr.Requests).IsEmpty();
    }

    [Test]
    public async Task TrackSearch_WithSoulseekDisabled_FallsBackToTorrents()
    {
        (MusicAcquisitionService acquisition, FakeHttpMessageHandler prowlarr, _, _) = Build(
            prowlarrResponder: _ => SearchJson(Release("Idina Menzel - Let It Go (Single) [FLAC]", seeders: 10)),
            configuration: SourcesConfig(torrents: true, soulseek: false));

        MusicGrabResult result = await acquisition.SearchTrackAndGrabAsync("Idina Menzel", "Let It Go");

        await Assert.That(prowlarr.Requests).IsNotEmpty();
        await Assert.That(result.Success).IsTrue();
    }

    [Test]
    public async Task GetTrackGrabStates_ReportsPerSongProgress()
    {
        (MusicAcquisitionService acquisition, _, _, TestMusicDb db) = Build(
            transmissionResponder: req =>
            {
                string body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                using JsonDocument doc = JsonDocument.Parse(body);
                return doc.RootElement.GetProperty("method").GetString() switch
                {
                    "torrent-get" => TransmissionOk("""
                        { "torrents": [ { "id": 5, "hashString": "abc123", "name": "Song", "percentDone": 0.5,
                          "labels": ["music"], "totalSize": 1000, "leftUntilDone": 500 } ] }
                        """),
                    _ => TransmissionOk()
                };
            });

        db.Seed(d => d.Set<GrabRecord>().Add(new GrabRecord
        {
            AlbumId = 0,
            Source = "torrent",
            Identifier = "abc123",
            ReleaseTitle = "Song",
            State = "Grabbed",
            DeezerTrackId = 555,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        }));

        Dictionary<long, TrackGrabState> states = await acquisition.GetTrackGrabStatesAsync();

        await Assert.That(states.ContainsKey(555)).IsTrue();
        await Assert.That(states[555].IsDownloading).IsTrue();
        await Assert.That(states[555].Size).IsEqualTo(1000);
        await Assert.That(states[555].Remaining).IsEqualTo(500);
        await Assert.That(states[555].Percent).IsEqualTo(50);
    }

    [Test]
    public async Task GetTrackGrabStates_IgnoresAFailedGrab()
    {
        // A failed Soulseek grab must not keep its song showing as "Downloading".
        (MusicAcquisitionService acquisition, _, _, TestMusicDb db) = Build();
        db.Seed(d => d.Set<GrabRecord>().Add(new GrabRecord
        {
            AlbumId = 0,
            Source = "soulseek",
            Identifier = "slskd:f:peer/let it go.flac",
            ReleaseTitle = "Let It Go",
            State = "Failed",
            DeezerTrackId = 72371930,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        }));

        Dictionary<long, TrackGrabState> states = await acquisition.GetTrackGrabStatesAsync();

        await Assert.That(states.ContainsKey(72371930)).IsFalse();
    }

    [Test]
    public async Task DurationsClose_AllowsSmallDeviations_RejectsADifferentRecording()
    {
        // A second or two of deviation is normal; a cover/remix/other song is not. Tolerance is the
        // smaller of 10% or 10s.
        await Assert.That(MusicAcquisitionService.DurationsClose(225, 224)).IsTrue();   // +1s
        await Assert.That(MusicAcquisitionService.DurationsClose(222, 224)).IsTrue();   // -2s
        await Assert.That(MusicAcquisitionService.DurationsClose(233, 224)).IsTrue();   // +9s
        await Assert.That(MusicAcquisitionService.DurationsClose(270, 224)).IsFalse();  // Everlast "Let It Go"
        await Assert.That(MusicAcquisitionService.DurationsClose(48, 166)).IsFalse();   // a 48s cover
        await Assert.That(MusicAcquisitionService.DurationsClose(31, 30)).IsTrue();     // short track, +1s
        await Assert.That(MusicAcquisitionService.DurationsClose(60, 30)).IsFalse();    // short track, doubled
    }

    [Test]
    public async Task DescribeSoulseekFailure_UsesTheSlskdErrorText()
    {
        Arrk.Slskd.SlskdDownload rejected = new()
        {
            State = "Completed, Rejected",
            Exception = "Transfer rejected: File not shared."
        };
        await Assert.That(MusicAcquisitionService.DescribeSoulseekFailure([rejected]))
            .IsEqualTo("failed — Transfer rejected: File not shared.");
    }

    [Test]
    public async Task DescribeTags_IncludesTheFilesOwnTags()
    {
        MusicTechnical probe = new()
        {
            TagArtist = "Nathan Lane",
            TagAlbum = "The Lion King",
            TagTitle = "Hakuna Matata"
        };

        await Assert.That(MusicAcquisitionService.DescribeTags(probe))
            .IsEqualTo(" — file tags: artist \"Nathan Lane\", album \"The Lion King\", title \"Hakuna Matata\"");
    }

    [Test]
    public async Task SearchAndGrab_NoSource_RecordsAnAuditOnlySearchEvent()
    {
        CapturingEventStore store = new();
        MusicNotifications notifications = new(store, deezer: null, NullLogger<MusicNotifications>.Instance);
        (MusicAcquisitionService acquisition, _, _, _) = Build(
            configuration: SourcesConfig(torrents: false, soulseek: false), notifications: notifications);

        MusicGrabResult result = await acquisition.SearchAndGrabAsync("Idina Menzel", "Let It Go", null, requireArtist: false);

        await Assert.That(result.Success).IsFalse();
        SystemEventRecord record = store.Records.Single();
        await Assert.That(record.Notification).IsNull();            // audit-only, no bell/Discord
        await Assert.That(record.EventType).IsEqualTo("music.release.missed");
        await Assert.That(record.Message).Contains("Let It Go");
    }

    [Test]
    public async Task SearchTrack_Soulseek_RecordsAGrabbedNotification()
    {
        FakeHttpMessageHandler slskdHandler = new();
        slskdHandler.Fallback = (req, _) =>
        {
            string path = req.RequestUri!.AbsolutePath;
            string json = path.Contains("/session")
                ? """{ "token": "t" }"""
                : path.EndsWith("/server")
                    ? """{ "isLoggedIn": true }"""
                    : path.Contains("/searches")
                        ? """
                          { "id": "s1", "isComplete": true, "responses": [
                            { "username": "peer1", "hasFreeUploadSlot": true, "queueLength": 0, "files": [
                              { "filename": "Music\\Disney\\Idina Menzel - Let It Go.flac", "size": 30000000, "bitRate": 0 } ] } ] }
                          """
                        : path.Contains("/transfers/downloads/")
                            ? """{ "ok": true }"""
                            : "[]";
            return Task.FromResult(FakeHttpMessageHandler.JsonResponse(json));
        };

        CapturingEventStore store = new();
        MusicNotifications notifications = new(store, deezer: null, NullLogger<MusicNotifications>.Instance);
        (MusicAcquisitionService acquisition, _, _, _) = Build(
            slskdEnabled: true, slskdHandler: slskdHandler, notifications: notifications);

        MusicGrabResult result = await acquisition.SearchTrackAndGrabAsync("Idina Menzel", "Let It Go");

        await Assert.That(result.Success).IsTrue();
        SystemEventRecord record = store.Records.Single(r => r.Topic == DownloadNotificationTopics.SoulseekGrabbed);
        await Assert.That(record.Notification).IsNotNull();
        await Assert.That(record.Notification!.Body).Contains("peer1");
    }

    sealed class CapturingEventStore : IEventStore
    {
        public List<SystemEventRecord> Records { get; } = [];

        public Task RecordAsync(SystemEventRecord record, CancellationToken ct = default)
        {
            Records.Add(record);
            return Task.CompletedTask;
        }

        public Task RecordAsync(IEnumerable<SystemEventRecord> records, CancellationToken ct = default)
        {
            Records.AddRange(records);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SystemEvent>> QueryAsync(SystemEventQuery query, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SystemEvent>>([]);

        public Task<int> PurgeOlderThanAsync(DateTime cutoff, CancellationToken ct = default) => Task.FromResult(0);

        public Task<IReadOnlyList<string>> GetEventTypesAsync(string? category = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }
}
