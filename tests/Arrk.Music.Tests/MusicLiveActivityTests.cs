using Arrk.Core.Services;
using Arrk.Music.Lyrics;
using Arrk.Music.Models;
using Arrk.Music.Services;
using Arrk.Slskd;
using Arrk.TestSupport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Arrk.Music.Tests;

/// <summary>
/// Covers the live activity the music acquisition queue publishes while it searches: one entry per
/// attempt, named after what it is looking for, carrying the source it is using, and cleared the
/// moment the search finishes.
/// </summary>
public class MusicLiveActivityTests
{
    const string SyncedRecord = """
        { "trackName": "Hysteria", "artistName": "Muse", "albumName": "Absolution", "duration": 213.0,
          "syncedLyrics": "[00:41.16] It's bugging me" }
        """;

    [Test]
    public async Task Sweep_PublishesASearchingLyricsActivity_NamingTheSongAndItsArtist()
    {
        // The activity title is the operation; the detail names the song and its artist.
        TestMusicDb db = new();
        using (db)
        {
            string root = Path.Combine(Path.GetTempPath(), "stackarr-lyrics-activity-" + Guid.NewGuid().ToString("N"));
            string file = Path.Combine(root, "Muse/Absolution/01 - Hysteria.flac");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "not really audio");

            db.Seed(context =>
            {
                MusicArtist artist = new() { Name = "Muse", SortName = "Muse", Added = DateTime.UtcNow };
                context.Set<MusicArtist>().Add(artist);
                context.SaveChanges();
                MusicAlbum album = new() { ArtistId = artist.Id, Title = "Absolution", Added = DateTime.UtcNow };
                context.Set<MusicAlbum>().Add(album);
                context.SaveChanges();
                context.Set<MusicTrack>().Add(new MusicTrack
                {
                    AlbumId = album.Id, ArtistId = artist.Id, Title = "Hysteria",
                    TrackNumber = 1, DurationSeconds = 213, FilePath = "Muse/Absolution/01 - Hysteria.flac", HasFile = true
                });
            });

            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["storage:music"] = root })
                .Build();
            MusicConfig music = new();
            music.Lyrics.RequestDelayMs = 0;

            LiveActivityRegistry liveActivities = new();
            List<LiveActivityEntry> seen = [];
            liveActivities.Changed += () => seen.AddRange(liveActivities.Active);

            LyricsService lyrics = new(
                db,
                new LrclibClient(NullLogger<LrclibClient>.Instance, new FakeHttpMessageHandler().EnqueueJson(SyncedRecord)),
                new MusicTestHelpers.StaticOptionsMonitor<MusicConfig>(music),
                configuration,
                liveActivities,
                new MusicNotifications(new FakeEventStore()),
                NullLogger<LyricsService>.Instance);

            try
            {
                await lyrics.SweepAsync();
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }

            await Assert.That(seen.Any(e => e.Title == "Searching lyrics")).IsTrue();
            await Assert.That(seen.Any(e => e.Detail is not null && e.Detail.Contains("\"Hysteria\"") && e.Detail.Contains("Muse"))).IsTrue();
            await Assert.That(seen.Any(e => e.Title.Contains("Downloading lyrics"))).IsFalse();
            await Assert.That(liveActivities.Count).IsEqualTo(0);   // cleared when the sweep ends
        }
    }

    static FakeHttpMessageHandler SlskdHandler()
    {
        FakeHttpMessageHandler handler = new();
        handler.Fallback = (req, _) =>
        {
            string path = req.RequestUri!.AbsolutePath;
            string json = path.Contains("/session")
                ? """{ "token": "t" }"""
                : path.EndsWith("/server")
                    ? """{ "isLoggedIn": true }"""
                    : path.Contains("/searches")
                        ? """{ "id": "s1", "isComplete": true, "responses": [] }"""
                        : "[]";
            return Task.FromResult(FakeHttpMessageHandler.JsonResponse(json));
        };
        return handler;
    }

    [Test]
    public async Task TrackSearch_PublishesASoulseekActivity_AndClearsIt()
    {
        TestMusicDb db = new();
        using (db)
        {
            MusicLibrary library = MusicTestHelpers.Library(db);
            SlskdService slskd = MusicTestHelpers.Slskd(SlskdHandler(), enabled: true);
            LiveActivityRegistry liveActivities = new();
            List<LiveActivityEntry> seen = [];
            liveActivities.Changed += () => seen.AddRange(liveActivities.Active);

            MusicAcquisitionService acquisition = MusicTestHelpers.Acquisition(
                library, db, MusicTestHelpers.EmptyHandler(), MusicTestHelpers.EmptyHandler(),
                SlskdHandler(), slskdEnabled: true, liveActivities: liveActivities);

            await acquisition.SearchTrackAndGrabAsync("Idina Menzel", "Let It Go");

            await Assert.That(seen.Any(e => e.Kind == LiveActivityKind.Search && e.Title == "Searching \"Let It Go\"")).IsTrue();
            await Assert.That(seen.Any(e => e.Kind == LiveActivityKind.Search && (e.Detail ?? "").Contains("Soulseek"))).IsTrue();
            await Assert.That(liveActivities.Count).IsEqualTo(0);
        }
    }

    [Test]
    public async Task AlbumSearch_PublishesAProwlarrActivity_AndClearsIt()
    {
        TestMusicDb db = new();
        using (db)
        {
            MusicLibrary library = MusicTestHelpers.Library(db);
            LiveActivityRegistry liveActivities = new();
            List<LiveActivityEntry> seen = [];
            liveActivities.Changed += () => seen.AddRange(liveActivities.Active);

            MusicAcquisitionService acquisition = MusicTestHelpers.Acquisition(
                library, db, MusicTestHelpers.EmptyHandler(), MusicTestHelpers.EmptyHandler(),
                liveActivities: liveActivities);

            await acquisition.SearchAndGrabAsync("Adele", "25");

            await Assert.That(seen.Any(e => e.Kind == LiveActivityKind.Search && e.Title == "Searching \"25\"")).IsTrue();
            await Assert.That(seen.Any(e => e.Kind == LiveActivityKind.Search && (e.Detail ?? "").Contains("Prowlarr"))).IsTrue();
            await Assert.That(liveActivities.Count).IsEqualTo(0);
        }
    }
}
