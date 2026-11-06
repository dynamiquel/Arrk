using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Arrk.Core;
using Arrk.Core.Models;
using Arrk.Core.Services;
using Arrk.Music.Lyrics;
using Arrk.Music.Models;
using Arrk.Music.Services;
using Arrk.TestSupport;

namespace Arrk.Music.Tests;

/// <summary>
/// Covers the lyrics sweep end to end over a real (in-memory) SQLite library and a fake LRCLIB: the
/// sidecar it writes, the state it records, and, the important part, that a provider outage is a
/// retryable failure while a genuine miss is final.
/// </summary>
public class LyricsServiceTests : IDisposable
{
    readonly TestMusicDb _db = new();
    readonly string _root = Path.Combine(Path.GetTempPath(), "stackarr-lyrics-" + Guid.NewGuid().ToString("N"));

    const string TrackPath = "Muse/Absolution/01 - Hysteria.flac";

    const string SyncedRecord = """
        {
          "trackName": "Hysteria",
          "artistName": "Muse",
          "albumName": "Absolution",
          "duration": 213.0,
          "instrumental": false,
          "syncedLyrics": "[00:41.16] It's bugging me",
          "hasWordSync": false
        }
        """;

    public LyricsServiceTests()
    {
        string file = Path.Combine(_root, TrackPath);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "not really audio");

        _db.Seed(db =>
        {
            MusicArtist artist = new() { Name = "Muse", SortName = "Muse", Added = DateTime.UtcNow };
            db.Set<MusicArtist>().Add(artist);
            db.SaveChanges();

            MusicAlbum album = new() { ArtistId = artist.Id, Title = "Absolution", Added = DateTime.UtcNow };
            db.Set<MusicAlbum>().Add(album);
            db.SaveChanges();

            db.Set<MusicTrack>().Add(new MusicTrack
            {
                AlbumId = album.Id,
                ArtistId = artist.Id,
                Title = "Hysteria",
                TrackNumber = 1,
                DurationSeconds = 213,
                FilePath = TrackPath,
                HasFile = true
            });
        });
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    LyricsService Service(FakeHttpMessageHandler handler, FakeEventStore? events = null)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["storage:music"] = _root })
            .Build();

        MusicConfig music = new();
        music.Lyrics.RequestDelayMs = 0;
        music.Lyrics.BatchSize = 10;

        return new LyricsService(
            _db,
            new LrclibClient(NullLogger<LrclibClient>.Instance, handler),
            new MusicTestHelpers.StaticOptionsMonitor<MusicConfig>(music),
            configuration,
            new LiveActivityRegistry(),
            new MusicNotifications(events ?? new FakeEventStore()),
            NullLogger<LyricsService>.Instance);
    }

    MusicTrack Track()
    {
        using ArrkDbContext db = _db.CreateDbContext();
        return db.Set<MusicTrack>().Single();
    }

    // Success

    [Test]
    public async Task Sweep_WritesTheSidecarBesideTheAudioAndRecordsTheTier()
    {
        FakeHttpMessageHandler handler = new FakeHttpMessageHandler().EnqueueJson(SyncedRecord);

        int written = await Service(handler).SweepAsync();

        await Assert.That(written).IsEqualTo(1);
        string sidecar = Path.Combine(_root, "Muse/Absolution/01 - Hysteria.lrc");
        await Assert.That(File.Exists(sidecar)).IsTrue();
        await Assert.That(await File.ReadAllTextAsync(sidecar)).Contains("It's bugging me");

        MusicTrack track = Track();
        await Assert.That(track.LyricsState).IsEqualTo(LyricsState.Found);
        await Assert.That(track.LyricsTier).IsEqualTo(LyricsTier.Line);
        await Assert.That(track.LyricsCheckedAt).IsNotNull();
    }

    [Test]
    public async Task Sweep_RecordsAnAuditEventPerLookupAndNotifiesOnSuccess()
    {
        FakeHttpMessageHandler handler = new FakeHttpMessageHandler().EnqueueJson(SyncedRecord);
        FakeEventStore events = new();

        await Service(handler, events).SweepAsync();

        await Assert.That(events.Records.Any(r => r.EventType == "music.lyrics.search.started")).IsTrue();
        await Assert.That(events.Records.Any(r => r.EventType == "music.lyrics.found")).IsTrue();
        // Only the success carries a notification; a lookup is not something to be pinged about.
        await Assert.That(events.Notifications.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Sweep_IsIdempotent_TracksThatAlreadyHaveLyricsAreNotLookedUpAgain()
    {
        FakeHttpMessageHandler handler = new FakeHttpMessageHandler()
            .EnqueueJson(SyncedRecord)
            .EnqueueJson(SyncedRecord);
        LyricsService service = Service(handler);

        await service.SweepAsync();
        int second = await service.SweepAsync();

        await Assert.That(second).IsEqualTo(0);
        await Assert.That(handler.Requests.Count).IsEqualTo(1);   // only the first sweep called out
    }

    // Miss vs failure

    [Test]
    public async Task Sweep_MarksAGenuineMissAsNotFoundAndWritesNoSidecar()
    {
        // Nothing is in the database: every call is a 404.
        FakeHttpMessageHandler handler = new FakeHttpMessageHandler { Fallback = (_, _) => Task.FromResult(FakeHttpMessageHandler.JsonResponse("""{"message":"not found"}""", HttpStatusCode.NotFound)) };

        int written = await Service(handler).SweepAsync();

        await Assert.That(written).IsEqualTo(0);
        await Assert.That(Track().LyricsState).IsEqualTo(LyricsState.NotFound);
        await Assert.That(File.Exists(Path.Combine(_root, "Muse/Absolution/01 - Hysteria.lrc"))).IsFalse();
    }

    [Test]
    public async Task Sweep_LeavesATrackEligibleWhenTheProviderIsUnavailable()
    {
        // A 503 after retries is not a verdict on the song: it must stay eligible rather than be
        // parked in a state that only the daily reset clears.
        FakeHttpMessageHandler handler = new FakeHttpMessageHandler
        {
            Fallback = (_, _) => Task.FromResult(FakeHttpMessageHandler.JsonResponse("""{"message":"busy"}""", HttpStatusCode.ServiceUnavailable))
        };

        await Service(handler).SweepAsync();

        MusicTrack track = Track();
        await Assert.That(track.LyricsState).IsEqualTo(LyricsState.None);
        await Assert.That(track.LyricsCheckedAt).IsNotNull();
    }

    [Test]
    public async Task Sweep_ResumesAtTheTracksItHasNotLookedAtYet()
    {
        // Tracks are swept least-recently-checked first, so a run that stopped early is picked up
        // where it left off rather than retrying the same songs that just failed.
        FakeHttpMessageHandler handler = new FakeHttpMessageHandler { Fallback = (_, _) => Task.FromResult(FakeHttpMessageHandler.JsonResponse("{}", HttpStatusCode.ServiceUnavailable)) };
        MusicConfig music = new();
        music.Lyrics.RequestDelayMs = 0;
        AddTracks(4);

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["storage:music"] = _root })
            .Build();
        LyricsService service = new(
            _db, new LrclibClient(NullLogger<LrclibClient>.Instance, handler),
            new MusicTestHelpers.StaticOptionsMonitor<MusicConfig>(music),
            configuration, new LiveActivityRegistry(),
            new MusicNotifications(new FakeEventStore()), NullLogger<LyricsService>.Instance);

        await service.SweepAsync();
        using (ArrkDbContext db = _db.CreateDbContext())
        {
            List<int> firstRun = [.. db.Set<MusicTrack>().Where(t => t.LyricsCheckedAt != null).Select(t => t.Id)];
            await Assert.That(firstRun.Count).IsEqualTo(3);   // stopped after three unavailable responses
        }

        await service.SweepAsync();
        using (ArrkDbContext db = _db.CreateDbContext())
        {
            List<int> secondRun = [.. db.Set<MusicTrack>().Where(t => t.LyricsCheckedAt != null).Select(t => t.Id)];
            // The second run reached the tracks the first never got to, not just the same three again.
            await Assert.That(secondRun.Count).IsGreaterThan(3);
        }
    }

    [Test]
    public async Task Sweep_MarksAFailureOnOurSideAsFailed_AndTheDailyResetClearsIt()
    {
        // A genuine failure (here: the sidecar cannot be written because the library path is blocked)
        // is what the daily retry exists for.
        FakeHttpMessageHandler handler = new FakeHttpMessageHandler().EnqueueJson(SyncedRecord);
        LyricsService service = Service(handler);

        Directory.Delete(Path.Combine(_root, "Muse"), recursive: true);
        File.WriteAllText(Path.Combine(_root, "Muse"), "blocker");

        await service.SweepAsync();
        await Assert.That(Track().LyricsState).IsEqualTo(LyricsState.Failed);

        int cleared = await service.ResetFailuresAsync();
        await Assert.That(cleared).IsEqualTo(1);
        await Assert.That(Track().LyricsState).IsEqualTo(LyricsState.None);
    }

    [Test]
    public async Task Sweep_StopsEarlyWhenTheProviderIsDown()
    {
        // Three consecutive unavailable responses end the run so the rest of the batch keeps its place.
        FakeHttpMessageHandler handler = new FakeHttpMessageHandler { Fallback = (_, _) => Task.FromResult(FakeHttpMessageHandler.JsonResponse("""{"message":"busy"}""", HttpStatusCode.ServiceUnavailable)) };
        MusicConfig music = new();
        music.Lyrics.RequestDelayMs = 0;
        AddTracks(5);

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["storage:music"] = _root })
            .Build();
        LyricsService service = new(
            _db, new LrclibClient(NullLogger<LrclibClient>.Instance, handler),
            new MusicTestHelpers.StaticOptionsMonitor<MusicConfig>(music),
            configuration, new LiveActivityRegistry(),
            new MusicNotifications(new FakeEventStore()), NullLogger<LyricsService>.Instance);

        await service.SweepAsync();

        using ArrkDbContext db = _db.CreateDbContext();
        int neverTouched = db.Set<MusicTrack>().Count(t => t.LyricsCheckedAt == null);
        await Assert.That(neverTouched).IsEqualTo(2);   // 5 tracks, 3 attempts, then the run stops
    }

    /// <summary>Adds <paramref name="count"/> extra placeholder tracks (2..N) to the seeded album.</summary>
    void AddTracks(int count)
    {
        _db.Seed(db =>
        {
            int albumId = db.Set<MusicAlbum>().Single().Id;
            int artistId = db.Set<MusicArtist>().Single().Id;
            for (int i = 2; i <= count; i++)
            {
                string relative = $"Muse/Absolution/{i:00} - Track {i}.flac";
                string full = Path.Combine(_root, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, "not really audio");
                db.Set<MusicTrack>().Add(new MusicTrack
                {
                    AlbumId = albumId, ArtistId = artistId, Title = $"Track {i}",
                    TrackNumber = i, DurationSeconds = 200, FilePath = relative, HasFile = true
                });
            }
        });
    }

    // Soundtrack credits

    const string FrozenSearchResult = """
        [
          { "trackName": "Let It Go", "artistName": "Instrumental Karaoke", "albumName": "Karaoke Hits", "duration": 224.5, "syncedLyrics": "[00:01.00] karaoke" },
          { "trackName": "Let It Go", "artistName": "Idina Menzel", "albumName": "Frozen (Original Motion Picture Soundtrack)", "duration": 225.0, "syncedLyrics": "[00:02.00] The snow glows white" }
        ]
        """;

    /// <summary>Retunes the seeded track to a different song (artist, album, title, duration).</summary>
    void RetuneSeed(string artist, string album, string title, int duration)
    {
        using ArrkDbContext db = _db.CreateDbContext();
        db.Set<MusicArtist>().Single().Name = artist;
        db.Set<MusicAlbum>().Single().Title = album;
        MusicTrack track = db.Set<MusicTrack>().Single();
        track.Title = title;
        track.DurationSeconds = duration;
        db.SaveChanges();
    }

    [Test]
    public async Task Sweep_ResolvesASoundtrackCreditedToVariousArtists()
    {
        // A soundtrack song credited "Various Artists" in the library. LRCLIB answers an
        // artist-filtered search for that credit with a 503, so the lookup searches the title alone
        // and accepts an exact title with a close duration, while refusing the karaoke version that
        // has an even closer length.
        RetuneSeed("Various Artists", "Frozen (Original Motion Picture Soundtrack)", "Let It Go (From \"Frozen\"/Soundtrack Version)", 224);
        FakeHttpMessageHandler handler = new FakeHttpMessageHandler
        {
            Fallback = (_, _) => Task.FromResult(FakeHttpMessageHandler.JsonResponse(FrozenSearchResult))
        };

        int written = await Service(handler).SweepAsync();

        await Assert.That(written).IsEqualTo(1);
        MusicTrack track = Track();
        await Assert.That(track.LyricsState).IsEqualTo(LyricsState.Found);
        await Assert.That(track.LyricsTier).IsEqualTo(LyricsTier.Line);

        string sidecar = await File.ReadAllTextAsync(Path.Combine(_root, "Muse/Absolution/01 - Hysteria.lrc"));
        await Assert.That(sidecar).Contains("snow glows white");
        await Assert.That(sidecar).DoesNotContain("karaoke");
    }

    [Test]
    public async Task Sweep_NeverAsksTheProviderToFilterByAPlaceholderArtist()
    {
        // "artist_name=Various Artists" earns a 503 from LRCLIB, so it must not be sent at all.
        RetuneSeed("Various Artists", "Frozen (Original Motion Picture Soundtrack)", "Let It Go", 224);
        FakeHttpMessageHandler handler = new FakeHttpMessageHandler { Fallback = (_, _) => Task.FromResult(FakeHttpMessageHandler.JsonResponse("[]")) };

        await Service(handler).SweepAsync();

        foreach (CapturedRequest request in handler.Requests)
            await Assert.That(Uri.UnescapeDataString(request.Uri.Query)).DoesNotContain("artist_name=Various Artists");
    }

    // Manual request

    [Test]
    public async Task Request_ForcesALookupForATrackThatWasMarkedNotFound()
    {
        // A curated manual request must work even though the sweep would skip a NotFound track.
        FakeHttpMessageHandler handler = new FakeHttpMessageHandler { Fallback = (_, _) => Task.FromResult(FakeHttpMessageHandler.JsonResponse("""{"message":"not found"}""", HttpStatusCode.NotFound)) };
        LyricsService service = Service(handler);
        await service.SweepAsync();
        await Assert.That(Track().LyricsState).IsEqualTo(LyricsState.NotFound);

        FakeHttpMessageHandler hit = new FakeHttpMessageHandler().EnqueueJson(SyncedRecord);
        LyricsRequestResult result = await Service(hit).RequestAsync(Track().Id);

        await Assert.That(result.Outcome).IsEqualTo(LyricsOutcome.Found);
        await Assert.That(Track().LyricsState).IsEqualTo(LyricsState.Found);
    }

    [Test]
    public async Task Request_ForAnUnknownTrack_ReportsItWithoutCallingTheProvider()
    {
        FakeHttpMessageHandler handler = new FakeHttpMessageHandler();
        LyricsRequestResult result = await Service(handler).RequestAsync(9999);

        await Assert.That(result.Outcome).IsEqualTo(LyricsOutcome.NotFound);
        await Assert.That(handler.Requests.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Sweep_ReplacesAStaleSidecarOfAnotherFormat()
    {
        // A previous plain-text sidecar must not be left next to a new LRC for Navidrome to pick up.
        string stale = Path.Combine(_root, "Muse/Absolution/01 - Hysteria.txt");
        await File.WriteAllTextAsync(stale, "old plain lyrics");
        FakeHttpMessageHandler handler = new FakeHttpMessageHandler().EnqueueJson(SyncedRecord);

        await Service(handler).SweepAsync();

        await Assert.That(File.Exists(stale)).IsFalse();
        await Assert.That(File.Exists(Path.Combine(_root, "Muse/Absolution/01 - Hysteria.lrc"))).IsTrue();
    }
}
