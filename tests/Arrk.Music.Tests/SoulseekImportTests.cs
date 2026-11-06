using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Arrk.Core;
using Arrk.Core.Models;
using Arrk.Core.Services;
using Arrk.Music.Models;
using Arrk.Music.Services;
using Arrk.Slskd;
using Arrk.TestSupport;

namespace Arrk.Music.Tests;

/// <summary>
/// Covers the Soulseek import path end-to-end at the service level: a grab records the album it is
/// for, the completed files are placed into that album with tags and a present flag, a manual or
/// leftover download is matched to a library album by folder name, and a failed or stalled source is
/// blacklisted and re-acquired.
/// </summary>
public class SoulseekImportTests
{
    sealed class RecordingBackgroundJobClient : IBackgroundJobClient
    {
        public List<Job> Created { get; } = [];
        public List<IState> States { get; } = [];

        public string Create(Job job, IState state)
        {
            Created.Add(job);
            States.Add(state);
            return Guid.NewGuid().ToString();
        }

        public bool ChangeState(string jobId, IState state, string expectedState) => true;
    }

    const string DandelionFolder = "Music\\Ella Langley\\Dandelion";

    static FakeHttpMessageHandler SlskdOk(string downloadsJson = "[]")
    {
        FakeHttpMessageHandler handler = new();
        handler.Fallback = (req, _) =>
        {
            string path = req.RequestUri!.AbsolutePath;
            string json = path.Contains("/session")
                ? """{ "token": "t" }"""
                : path.EndsWith("/server")
                    ? """{ "isLoggedIn": true }"""
                    : path.EndsWith("/transfers/downloads")
                        ? downloadsJson
                        : "[]";
            return Task.FromResult(FakeHttpMessageHandler.JsonResponse(json));
        };
        return handler;
    }

    static (MusicAcquisitionService Acquisition, MusicLibrary Library, TestMusicDb Db) Build(
        FakeHttpMessageHandler? slskdHandler = null,
        RecordingBackgroundJobClient? jobs = null,
        FakeHttpMessageHandler? deezerHandler = null,
        SlskdConfig? slskdConfig = null)
    {
        TestMusicDb db = new();
        MusicLibrary library = MusicTestHelpers.Library(db);
        MusicAcquisitionService acquisition = MusicTestHelpers.Acquisition(
            library,
            db,
            MusicTestHelpers.EmptyHandler(),
            MusicTestHelpers.EmptyHandler(),
            slskdHandler ?? SlskdOk(),
            slskdEnabled: true,
            configuration: new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["storage:base"] = Path.GetTempPath(),
                    ["storage:downloads"] = Path.Combine(Path.GetTempPath(), "stackarr-dl-" + Guid.NewGuid().ToString("N"))
                }).Build(),
            backgroundJobs: jobs,
            deezerHandler: deezerHandler,
            slskdConfig: slskdConfig);
        return (acquisition, library, db);
    }

    static int SeedAlbum(TestMusicDb db, string title, params string[] tracks)
    {
        int id = 0;
        db.Seed(d =>
        {
            MusicArtist artist = new() { Name = "Ella Langley", SortName = "Langley, Ella", Added = DateTime.UtcNow };
            MusicAlbum album = new() { Title = title, Monitored = true, TotalTrackCount = tracks.Length };
            for (int i = 0; i < tracks.Length; i++)
                album.Tracks.Add(new MusicTrack { Title = tracks[i], TrackNumber = i + 1, AbsoluteTrackNumber = i + 1 });
            artist.Albums.Add(album);
            d.Set<MusicArtist>().Add(artist);
            d.SaveChanges();
            id = album.Id;
        });
        return id;
    }

    // Grab records the target

    [Test]
    public async Task GrabSoulseek_RecordsAlbumAndDownloadKey()
    {
        (MusicAcquisitionService acquisition, _, TestMusicDb db) = Build();
        using (db)
        {
            int albumId = SeedAlbum(db, "Dandelion", "Dandelion", "Choosin' Texas");

            MusicGrabResult result = await acquisition.GrabSoulseekAsync(
                "Ella Langley", "Dandelion", albumId, "peer1",
                [new SlskdFile { Filename = $"{DandelionFolder}\\01 Dandelion.flac", Size = 30_000_000 }],
                "Ella Langley - Dandelion");

            await Assert.That(result.Success).IsTrue();

            using ArrkDbContext context = db.CreateDbContext();
            GrabRecord? record = context.Set<GrabRecord>().SingleOrDefault(g => g.Source == "soulseek");
            await Assert.That(record).IsNotNull();
            await Assert.That(record!.AlbumId).IsEqualTo(albumId);
            await Assert.That(record.Identifier).IsEqualTo(
                MusicAcquisitionService.SoulseekFileKey($"{DandelionFolder}\\01 Dandelion.flac"));
        }
    }

    [Test]
    public async Task GrabSoulseek_BlacklistIsPerSong_NeverBansTheArtistOrPeer()
    {
        (MusicAcquisitionService acquisition, _, TestMusicDb db) = Build();
        using (db)
        {
            const string path = "Music\\Elton John\\01 - Can You Feel the Love Tonight.flac";
            string key = MusicAcquisitionService.SoulseekFileKey(path);
            db.Seed(d => d.Set<GrabRecord>().Add(new GrabRecord
            {
                AlbumId = 0,
                Source = "soulseek",
                Identifier = key,
                ReleaseTitle = "Can You Feel the Love Tonight",
                State = "Failed",
                DeezerTrackId = 72371930,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            }));

            // Same song request → that source is skipped.
            MusicGrabResult same = await acquisition.GrabSoulseekAsync(
                "Elton John", "Can You Feel the Love Tonight", 555, "peer1",
                [new SlskdFile { Filename = path, Size = 1 }], "Elton John", deezerTrackId: 72371930);
            await Assert.That(same.Success).IsFalse();

            // A different song → the same peer/file is not banned.
            MusicGrabResult other = await acquisition.GrabSoulseekAsync(
                "Elton John", "Can You Feel the Love Tonight", 555, "peer1",
                [new SlskdFile { Filename = path, Size = 1 }], "Elton John", deezerTrackId: 999999);
            await Assert.That(other.Success).IsTrue();
        }
    }

    [Test]
    public async Task GrabSoulseek_BlacklistExpires_SoASourceCanBeRetriedLater()
    {
        (MusicAcquisitionService acquisition, _, TestMusicDb db) = Build();
        using (db)
        {
            const string path = "Music\\Elton John\\01 - Can You Feel the Love Tonight.flac";
            string key = MusicAcquisitionService.SoulseekFileKey(path);
            db.Seed(d => d.Set<GrabRecord>().Add(new GrabRecord
            {
                AlbumId = 0,
                Source = "soulseek",
                Identifier = key,
                ReleaseTitle = "Can You Feel the Love Tonight",
                State = "Failed",
                DeezerTrackId = 72371930,
                CreatedAt = DateTime.UtcNow.AddDays(-8),
                UpdatedAt = DateTime.UtcNow.AddDays(-8)
            }));

            MusicGrabResult result = await acquisition.GrabSoulseekAsync(
                "Elton John", "Can You Feel the Love Tonight", 555, "peer1",
                [new SlskdFile { Filename = path, Size = 1 }], "Elton John", deezerTrackId: 72371930);

            await Assert.That(result.Success).IsTrue();
        }
    }

    [Test]
    public async Task SlskdProviderGrab_RecordsAlbumViaAcquisition()
    {
        FakeHttpMessageHandler handler = SlskdOk();
        (MusicAcquisitionService acquisition, _, TestMusicDb db) = Build(handler);
        using (db)
        {
            int albumId = SeedAlbum(db, "Dandelion", "Dandelion");
            SlskdService slskd = new(
                new SlskdClient("http://slskd:5030", "u", "p", handler),
                new MusicTestHelpers.StaticOptionsMonitor<SlskdConfig>(new SlskdConfig { Enabled = true }),
                new ServiceRegistry(),
                NullLogger<SlskdService>.Instance);
            SlskdReleaseProvider provider = new(
                slskd,
                NullLogger<SlskdReleaseProvider>.Instance,
                acquisition);

            ReleaseCandidate release = new()
            {
                Id = "slskd:peer1:dandelion",
                Title = "Ella Langley - Dandelion",
                Source = "Soulseek",
                Indexer = "Soulseek · peer1",
                SourceFiles = [new ReleaseSourceFile("peer1", $"{DandelionFolder}\\01 Dandelion.flac", 1)]
            };
            ReleaseSearchQuery query = new()
            {
                Domain = MusicMediaDomains.Music, Kind = MusicReleaseKinds.Album,
                Artist = "Ella Langley",
                Album = "Dandelion",
                LibraryId = albumId.ToString()
            };

            ReleaseGrabResult result = await provider.GrabAsync(release, query);

            await Assert.That(result.Success).IsTrue();
            using ArrkDbContext context = db.CreateDbContext();
            GrabRecord? record = context.Set<GrabRecord>().SingleOrDefault(g => g.Source == "soulseek");
            await Assert.That(record).IsNotNull();
            await Assert.That(record!.AlbumId).IsEqualTo(albumId);
            await Assert.That(record.Identifier).IsEqualTo(
                MusicAcquisitionService.SoulseekFileKey($"{DandelionFolder}\\01 Dandelion.flac"));
        }
    }

    [Test]
    public async Task GrabSoulseek_RefusesBlacklistedSource()
    {
        (MusicAcquisitionService acquisition, _, TestMusicDb db) = Build();
        using (db)
        {
            int albumId = SeedAlbum(db, "Dandelion", "Dandelion");
            db.Seed(d => d.Set<GrabRecord>().Add(new GrabRecord
            {
                AlbumId = albumId,
                Source = "soulseek",
                Identifier = MusicAcquisitionService.SoulseekFileKey($"{DandelionFolder}\\01 Dandelion.flac"),
                ReleaseTitle = "Ella Langley - Dandelion",
                State = "Failed",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            }));

            MusicGrabResult result = await acquisition.GrabSoulseekAsync(
                "Ella Langley", "Dandelion", albumId, "peer1",
                [new SlskdFile { Filename = $"{DandelionFolder}\\01 Dandelion.flac", Size = 1 }],
                "Ella Langley - Dandelion");

            await Assert.That(result.Success).IsFalse();
        }
    }

    // Import places into the grabbed album

    [Test]
    public async Task ImportSoulseek_PlacesIntoAlbum_AndMarksTracksPresent()
    {
        string root = Path.Combine(Path.GetTempPath(), "stackarr-slsk-" + Guid.NewGuid().ToString("N"));
        string soulseek = Path.Combine(root, "downloads", "slskd");
        string library = Path.Combine(root, "library");
        string leaf = Path.Combine(soulseek, "Dandelion");
        Directory.CreateDirectory(leaf);
        await File.WriteAllTextAsync(Path.Combine(leaf, "01 Dandelion.flac"), "a");
        await File.WriteAllTextAsync(Path.Combine(leaf, "02 Choosin' Texas.flac"), "b");

        (MusicAcquisitionService acquisition, MusicLibrary lib, TestMusicDb db) = Build();
        using (db)
        {
            int albumId = SeedAlbum(db, "Dandelion", "Dandelion", "Choosin' Texas");
            db.Seed(d => d.Set<GrabRecord>().Add(new GrabRecord
            {
                AlbumId = albumId,
                Source = "soulseek",
                Identifier = "slskd:dandelion",
                ReleaseTitle = "Ella Langley - Dandelion",
                State = "Grabbed",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            }));
            acquisition.SoulseekRoot = soulseek;
            acquisition.LibraryRoot = library;

            try
            {
                int imported = await acquisition.ImportSoulseekAsync();

                await Assert.That(imported).IsEqualTo(2);
                await Assert.That(File.Exists(Path.Combine(library, "Ella Langley", "Dandelion", "01 - Dandelion.flac"))).IsTrue();
                await Assert.That(File.Exists(Path.Combine(library, "Ella Langley", "Dandelion", "02 - Choosin' Texas.flac"))).IsTrue();
                Album? album = await lib.GetAlbumAsync(albumId);
                await Assert.That(album).IsNotNull();
                await Assert.That(album!.Tracks.Count(t => t.HasFile)).IsEqualTo(2);

                using ArrkDbContext context = db.CreateDbContext();
                GrabRecord record = context.Set<GrabRecord>().Single(g => g.Source == "soulseek");
                await Assert.That(record.State).IsEqualTo("Imported");
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Test]
    public async Task ImportSoulseek_MatchesLeftoverFolder_ToLibraryAlbum()
    {
        string root = Path.Combine(Path.GetTempPath(), "stackarr-slsk-" + Guid.NewGuid().ToString("N"));
        string soulseek = Path.Combine(root, "downloads", "slskd");
        string library = Path.Combine(root, "library");
        string leaf = Path.Combine(soulseek, "Dandelion (2026) [FLAC]");
        Directory.CreateDirectory(leaf);
        await File.WriteAllTextAsync(Path.Combine(leaf, "01 Dandelion.flac"), "a");

        (MusicAcquisitionService acquisition, MusicLibrary lib, TestMusicDb db) = Build();
        using (db)
        {
            int albumId = SeedAlbum(db, "Dandelion", "Dandelion");
            acquisition.SoulseekRoot = soulseek;
            acquisition.LibraryRoot = library;

            try
            {
                // No grab record, the importer must recover the album from the folder name.
                int imported = await acquisition.ImportSoulseekAsync();

                await Assert.That(imported).IsEqualTo(1);
                Album? album = await lib.GetAlbumAsync(albumId);
                await Assert.That(album!.Tracks.Count(t => t.HasFile)).IsEqualTo(1);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Test]
    public async Task ImportSoulseek_NotOnDeezer_IsNotImported()
    {
        string root = Path.Combine(Path.GetTempPath(), "stackarr-slsk-" + Guid.NewGuid().ToString("N"));
        string soulseek = Path.Combine(root, "downloads", "slskd");
        string library = Path.Combine(root, "library");
        string leaf = Path.Combine(soulseek, "Daft Punk - Discovery (2001)");
        Directory.CreateDirectory(leaf);
        await File.WriteAllTextAsync(Path.Combine(leaf, "01 One More Time.flac"), "a");
        await File.WriteAllTextAsync(Path.Combine(leaf, "02 Aerodynamic.flac"), "b");

        (MusicAcquisitionService acquisition, _, TestMusicDb db) = Build();
        using (db)
        {
            acquisition.SoulseekRoot = soulseek;
            acquisition.LibraryRoot = library;

            try
            {
                int imported = await acquisition.ImportSoulseekAsync();

                // Not identified on Deezer → nothing is imported (no unidentified albums exist).
                await Assert.That(imported).IsEqualTo(0);
                await Assert.That(Directory.Exists(library)).IsFalse();
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    // Failure detection

    static string QueuedDownloadsJson(string leaf, bool started, long transferred) => $$"""
        [ { "username": "peer1", "directories": [ { "directory": "Music\\Ella Langley\\{{leaf}}",
            "files": [ { "id": "f1", "username": "peer1",
              "filename": "Music\\Ella Langley\\{{leaf}}\\01 Dandelion.flac",
              "state": "{{(started ? "InProgress" : "Queued, Remotely")}}",
              "percentComplete": 0, "size": 1000, "bytesTransferred": {{transferred}},
              "{{(started ? "startedAt" : "enqueuedAt")}}": "2020-01-01T00:00:00" } ] } ] } ]
        """;

    /// <summary>A completed single-file download that the peer rejected (e.g. "File not shared").</summary>
    static string RejectedSingleFileJson(string remotePath, string exception) => $$"""
        [ { "username": "peer1", "directories": [ { "directory": {{System.Text.Json.JsonSerializer.Serialize(System.IO.Path.GetDirectoryName(remotePath) ?? "")}},
            "files": [ { "id": "f1", "username": "peer1", "filename": {{System.Text.Json.JsonSerializer.Serialize(remotePath)}},
              "state": "Completed, Rejected", "percentComplete": 0, "size": 1000, "bytesTransferred": 0,
              "exception": {{System.Text.Json.JsonSerializer.Serialize(exception)}} } ] } ] } ]
        """;

    [Test]
    public async Task CheckSoulseekFailures_RejectedSingleFile_RequeuesTheRequest()
    {
        // A rejected single-file transfer is keyed by file, not folder: it must be matched to its grab
        // and put back on the acquisition queue, not left looking like it has "no transfer" for 45 min.
        const string remotePath = "Music\\Various\\Hakuna Matata.flac";
        string key = MusicAcquisitionService.SoulseekFileKey(remotePath);
        (MusicAcquisitionService acquisition, _, TestMusicDb db) =
            Build(SlskdOk(RejectedSingleFileJson(remotePath, "Transfer rejected: File not shared.")));
        using (db)
        {
            db.Seed(d => d.Set<GrabRecord>().Add(new GrabRecord
            {
                AlbumId = 0,
                Source = "soulseek",
                Identifier = key,
                ReleaseTitle = "Hakuna Matata",
                State = "Grabbed",
                DeezerTrackId = 0,
                CreatedAt = DateTime.UtcNow.AddMinutes(-5),
                UpdatedAt = DateTime.UtcNow.AddMinutes(-5)
            }));

            int requeued = await acquisition.CheckSoulseekFailuresAsync();

            await Assert.That(requeued).IsEqualTo(1);
            using ArrkDbContext context = db.CreateDbContext();
            await Assert.That(context.Set<GrabRecord>().Single().State).IsEqualTo("Failed");
            MusicAcquireQueueItem item = context.Set<MusicAcquireQueueItem>().Single();
            await Assert.That(item.Attempts).IsEqualTo(1);
            await Assert.That(item.InFlight).IsFalse();
        }
    }

    [Test]
    public async Task CheckSoulseekFailures_QueuedWithNoProgress_RequeuesTheAlbum()
    {
        (MusicAcquisitionService acquisition, _, TestMusicDb db) =
            Build(SlskdOk(QueuedDownloadsJson("Dandelion", started: false, transferred: 0)));
        using (db)
        {
            int albumId = SeedAlbum(db, "Dandelion", "Dandelion");
            db.Seed(d => d.Set<GrabRecord>().Add(new GrabRecord
            {
                AlbumId = albumId,
                Source = "soulseek",
                Identifier = "slskd:dandelion",
                ReleaseTitle = "Ella Langley - Dandelion",
                State = "Grabbed",
                CreatedAt = DateTime.UtcNow.AddHours(-2),
                UpdatedAt = DateTime.UtcNow.AddHours(-2)
            }));

            int requeued = await acquisition.CheckSoulseekFailuresAsync();

            await Assert.That(requeued).IsEqualTo(1);
            using ArrkDbContext context = db.CreateDbContext();
            await Assert.That(context.Set<GrabRecord>().Single(g => g.Source == "soulseek").State).IsEqualTo("Failed");
            MusicAcquireQueueItem item = context.Set<MusicAcquireQueueItem>().Single();
            await Assert.That(item.Key).IsEqualTo($"album:{albumId}");
        }
    }

    [Test]
    public async Task CheckSoulseekFailures_SongGrabWithoutAlbum_KeepsTheRealArtist()
    {
        // Re-queuing a failed song grab keeps the requested artist (resolved from the Deezer track id)
        // rather than searching the title alone.
        (MusicAcquisitionService acquisition, _, TestMusicDb db) = Build(
            SlskdOk(QueuedDownloadsJson("Dandelion", started: false, transferred: 0)),
            deezerHandler: DeezerTrack(72371930, "Let It Go", "Idina Menzel"));
        using (db)
        {
            db.Seed(d => d.Set<GrabRecord>().Add(new GrabRecord
            {
                AlbumId = 0,
                Source = "soulseek",
                Identifier = "slskd:dandelion",
                ReleaseTitle = "Let It Go",
                State = "Grabbed",
                DeezerTrackId = 72371930,
                CreatedAt = DateTime.UtcNow.AddHours(-2),
                UpdatedAt = DateTime.UtcNow.AddHours(-2)
            }));

            int requeued = await acquisition.CheckSoulseekFailuresAsync();

            await Assert.That(requeued).IsEqualTo(1);
            using ArrkDbContext context = db.CreateDbContext();
            MusicAcquireQueueItem item = context.Set<MusicAcquireQueueItem>().Single();
            await Assert.That(item.Key).IsEqualTo("track:72371930");
            await Assert.That(item.Artist).IsEqualTo("Idina Menzel");
        }
    }

    static FakeHttpMessageHandler DeezerTrack(long id, string title, string artist)
    {
        FakeHttpMessageHandler handler = new();
        handler.Fallback = (_, _) => Task.FromResult(FakeHttpMessageHandler.JsonResponse(
            $$"""{ "id": {{id}}, "title": "{{title}}", "artist": { "id": 1, "name": "{{artist}}" }, "album": { "id": 5 } }"""));
        return handler;
    }

    [Test]
    public async Task CheckSoulseekFailures_RaisesTheAttemptCount_SoTheRequestSinks()
    {
        // Failures accumulate in the queue row: a request that has already failed 3 times moves to
        // attempt 4, which sorts it behind fresher requests.
        const string remotePath = "Music\\Various\\Hakuna Matata.flac";
        string key = MusicAcquisitionService.SoulseekFileKey(remotePath);
        (MusicAcquisitionService acquisition, _, TestMusicDb db) =
            Build(SlskdOk(RejectedSingleFileJson(remotePath, "Transfer rejected: Not shared")));
        using (db)
        {
            db.Seed(d =>
            {
                d.Set<MusicAcquireQueueItem>().Add(new MusicAcquireQueueItem
                {
                    Key = "name:|hakunamatata",
                    Title = "Hakuna Matata",
                    Attempts = 3,
                    RequestedAt = DateTime.UtcNow.AddMinutes(-30)
                });
                d.Set<GrabRecord>().Add(new GrabRecord
                {
                    AlbumId = 0,
                    Source = "soulseek",
                    Identifier = key,
                    ReleaseTitle = "Hakuna Matata",
                    State = "Grabbed",
                    DeezerTrackId = 0,
                    CreatedAt = DateTime.UtcNow.AddMinutes(-5),
                    UpdatedAt = DateTime.UtcNow.AddMinutes(-5)
                });
            });

            await acquisition.CheckSoulseekFailuresAsync();

            using ArrkDbContext context = db.CreateDbContext();
            await Assert.That(context.Set<MusicAcquireQueueItem>().Single().Attempts).IsEqualTo(4);
        }
    }

    [Test]
    public async Task CheckSoulseekFailures_WhenProgressing_DoesNotFail()
    {
        RecordingBackgroundJobClient jobs = new();
        (MusicAcquisitionService acquisition, _, TestMusicDb db) =
            Build(SlskdOk(QueuedDownloadsJson("Dandelion", started: true, transferred: 500)), jobs);
        using (db)
        {
            int albumId = SeedAlbum(db, "Dandelion", "Dandelion");
            db.Seed(d => d.Set<GrabRecord>().Add(new GrabRecord
            {
                AlbumId = albumId,
                Source = "soulseek",
                Identifier = "slskd:dandelion",
                ReleaseTitle = "Ella Langley - Dandelion",
                State = "Grabbed",
                CreatedAt = DateTime.UtcNow.AddHours(-2),
                UpdatedAt = DateTime.UtcNow.AddHours(-2)
            }));

            int requeued = await acquisition.CheckSoulseekFailuresAsync();

            await Assert.That(requeued).IsEqualTo(0);
            await Assert.That(jobs.Created).IsEmpty();
        }
    }

    // Unknown folders bind to the library

    [Test]
    public async Task ImportSoulseek_UnknownFolder_NotOnDeezer_CreatesNoAlbum()
    {
        string root = Path.Combine(Path.GetTempPath(), "stackarr-slsk-" + Guid.NewGuid().ToString("N"));
        string soulseek = Path.Combine(root, "downloads", "slskd");
        string library = Path.Combine(root, "library");
        string leaf = Path.Combine(soulseek, "Daft Punk - Discovery");
        Directory.CreateDirectory(leaf);
        await File.WriteAllTextAsync(Path.Combine(leaf, "01 One More Time.flac"), "a");
        await File.WriteAllTextAsync(Path.Combine(leaf, "02 Aerodynamic.flac"), "b");

        // No grab record and no Deezer match (the default handler returns nothing).
        (MusicAcquisitionService acquisition, MusicLibrary lib, TestMusicDb db) = Build();
        using (db)
        {
            acquisition.SoulseekRoot = soulseek;
            acquisition.LibraryRoot = library;

            try
            {
                int imported = await acquisition.ImportSoulseekAsync();

                await Assert.That(imported).IsEqualTo(0);
                // No unidentified library album is ever created.
                Album? created = await lib.FindAlbumByTitleAsync("Daft Punk", "Discovery");
                await Assert.That(created).IsNull();
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Test]
    public async Task ImportSoulseek_UnknownFolder_BindsDeezerAlbumWhenFound()
    {
        string root = Path.Combine(Path.GetTempPath(), "stackarr-slsk-" + Guid.NewGuid().ToString("N"));
        string soulseek = Path.Combine(root, "downloads", "slskd");
        string library = Path.Combine(root, "library");
        string leaf = Path.Combine(soulseek, "Daft Punk - Discovery");
        Directory.CreateDirectory(leaf);
        await File.WriteAllTextAsync(Path.Combine(leaf, "01 One More Time.flac"), "a");

        FakeHttpMessageHandler deezer = new();
        deezer.Fallback = (req, _) =>
        {
            string path = req.RequestUri!.AbsolutePath;
            string json = path.Contains("search/album")
                ? """{ "data": [ { "id": 123, "title": "Discovery", "artist": { "id": 9, "name": "Daft Punk" } } ] }"""
                : path.Contains("/album/123/tracks")
                    ? """{ "data": [ { "id": 1001, "title": "One More Time", "track_position": 1, "disk_number": 1, "duration": 320 } ] }"""
                    : """{ "id": 123, "title": "Discovery", "artist": { "id": 9, "name": "Daft Punk" }, "nb_tracks": 1, "record_type": "album", "release_date": "2001-03-12", "cover_xl": "http://cdn/d.jpg" }""";
            return Task.FromResult(FakeHttpMessageHandler.JsonResponse(json));
        };

        (MusicAcquisitionService acquisition, MusicLibrary lib, TestMusicDb db) = Build(deezerHandler: deezer);
        using (db)
        {
            acquisition.SoulseekRoot = soulseek;
            acquisition.LibraryRoot = library;

            try
            {
                int imported = await acquisition.ImportSoulseekAsync();

                await Assert.That(imported).IsEqualTo(1);
                Album? created = await lib.FindAlbumByTitleAsync("Daft Punk", "Discovery");
                await Assert.That(created).IsNotNull();
                // Bound to Deezer: real id + track list, so it counts as identified.
                await Assert.That(created!.DeezerId).IsEqualTo(123);
                await Assert.That(created.Tracks.Count).IsEqualTo(1);
                await Assert.That(created.Tracks.All(t => !t.IsUnidentified)).IsTrue();
                await Assert.That(created.Tracks[0].HasFile).IsTrue();
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Test]
    public async Task ImportSoulseek_RemovesCompletedSourceAfterImport()
    {
        string root = Path.Combine(Path.GetTempPath(), "stackarr-slsk-" + Guid.NewGuid().ToString("N"));
        string soulseek = Path.Combine(root, "downloads", "slskd");
        string library = Path.Combine(root, "library");
        string leaf = Path.Combine(soulseek, "Daft Punk - Discovery");
        Directory.CreateDirectory(leaf);
        await File.WriteAllTextAsync(Path.Combine(leaf, "01 One More Time.flac"), "a");

        FakeHttpMessageHandler deezer = new();
        deezer.Fallback = (req, _) =>
        {
            string path = req.RequestUri!.AbsolutePath;
            string json = path.Contains("search/album")
                ? """{ "data": [ { "id": 123, "title": "Discovery", "artist": { "id": 9, "name": "Daft Punk" } } ] }"""
                : path.Contains("/album/123/tracks")
                    ? """{ "data": [ { "id": 1001, "title": "One More Time", "track_position": 1, "disk_number": 1 } ] }"""
                    : """{ "id": 123, "title": "Discovery", "artist": { "id": 9, "name": "Daft Punk" }, "nb_tracks": 1, "record_type": "album" }""";
            return Task.FromResult(FakeHttpMessageHandler.JsonResponse(json));
        };

        (MusicAcquisitionService acquisition, MusicLibrary lib, TestMusicDb db) = Build(deezerHandler: deezer);
        using (db)
        {
            acquisition.SoulseekRoot = soulseek;
            acquisition.LibraryRoot = library;

            try
            {
                await acquisition.ImportSoulseekAsync();

                // The staged download copy is removed (the library copy is a hard link), so the
                // completed Soulseek list can't grow forever.
                await Assert.That(Directory.Exists(leaf)).IsFalse();
                await Assert.That(File.Exists(Path.Combine(library, "Daft Punk", "Discovery", "01 - One More Time.flac"))).IsTrue();
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Test]
    public async Task ImportSoulseek_KeepsSource_WhenRemovalDisabled()
    {
        string root = Path.Combine(Path.GetTempPath(), "stackarr-slsk-" + Guid.NewGuid().ToString("N"));
        string soulseek = Path.Combine(root, "downloads", "slskd");
        string library = Path.Combine(root, "library");
        string leaf = Path.Combine(soulseek, "Daft Punk - Discovery");
        Directory.CreateDirectory(leaf);
        await File.WriteAllTextAsync(Path.Combine(leaf, "01 One More Time.flac"), "a");

        (MusicAcquisitionService acquisition, _, TestMusicDb db) =
            Build(slskdConfig: new SlskdConfig { Enabled = true, RemoveCompletedAfterImport = false });
        using (db)
        {
            acquisition.SoulseekRoot = soulseek;
            acquisition.LibraryRoot = library;

            try
            {
                await acquisition.ImportSoulseekAsync();

                await Assert.That(Directory.Exists(leaf)).IsTrue();
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Test]
    public async Task ImportSoulseek_AlreadyInLibrary_RemovesSourceWithoutPlacing()
    {
        string root = Path.Combine(Path.GetTempPath(), "stackarr-slsk-" + Guid.NewGuid().ToString("N"));
        string soulseek = Path.Combine(root, "downloads", "slskd");
        string library = Path.Combine(root, "library");
        string leaf = Path.Combine(soulseek, "Daft Punk - Discovery");
        Directory.CreateDirectory(leaf);
        await File.WriteAllTextAsync(Path.Combine(leaf, "01 One More Time.flac"), "a");
        // The file is already in the library, so this run places nothing new.
        string dest = Path.Combine(library, "Daft Punk - Discovery");
        Directory.CreateDirectory(dest);
        await File.WriteAllTextAsync(Path.Combine(dest, "01 One More Time.flac"), "existing");

        (MusicAcquisitionService acquisition, _, TestMusicDb db) = Build();
        using (db)
        {
            acquisition.SoulseekRoot = soulseek;
            acquisition.LibraryRoot = library;

            try
            {
                int imported = await acquisition.ImportSoulseekAsync();

                await Assert.That(imported).IsEqualTo(0);
                // Even with nothing newly placed, the staged download is cleaned up so the completed
                // list converges instead of being retried forever.
                await Assert.That(Directory.Exists(leaf)).IsFalse();
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    // A single requested song trusts its identity

    [Test]
    public async Task ImportSoulseek_SingleRequestedTrack_BindsToItsDeezerAlbum()
    {
        string root = Path.Combine(Path.GetTempPath(), "stackarr-slsk-" + Guid.NewGuid().ToString("N"));
        string soulseek = Path.Combine(root, "downloads", "slskd");
        string library = Path.Combine(root, "library");
        string leaf = Path.Combine(soulseek, "Disney");
        Directory.CreateDirectory(leaf);
        await File.WriteAllTextAsync(Path.Combine(leaf, "Idina Menzel - Let It Go.flac"), "a");

        FakeHttpMessageHandler deezer = new();
        deezer.Fallback = (req, _) =>
        {
            string path = req.RequestUri!.AbsolutePath;
            string json = path.Contains("search/track")
                ? """{ "data": [ { "id": 500, "title": "Let It Go (From \"Frozen\"/Soundtrack Version)", "artist": { "id": 7023, "name": "Idina Menzel" }, "album": { "id": 999, "title": "Frozen (Original Motion Picture Soundtrack)" } } ] }"""
                : path.Contains("album/999/tracks")
                    ? """{ "data": [ { "id": 500, "title": "Let It Go (From \"Frozen\"/Soundtrack Version)", "track_position": 1, "disk_number": 1 } ] }"""
                    : """{ "id": 999, "title": "Frozen (Original Motion Picture Soundtrack)", "artist": { "id": 1, "name": "Various Artists" }, "nb_tracks": 1, "record_type": "album" }""";
            return Task.FromResult(FakeHttpMessageHandler.JsonResponse(json));
        };

        (MusicAcquisitionService acquisition, _, TestMusicDb db) = Build(deezerHandler: deezer);
        using (db)
        {
            acquisition.SoulseekRoot = soulseek;
            acquisition.LibraryRoot = library;

            // The automatic track grab records the requested song; the importer must trust that, not
            // the peer's "Disney" folder, and bind to the album the song actually belongs to.
            MusicGrabResult grab = await acquisition.GrabSoulseekAsync(
                "Idina Menzel", "Let It Go", null, "peer1",
                [new SlskdFile { Filename = "Music\\Disney\\Idina Menzel - Let It Go.flac", Size = 1 }],
                "Let It Go");
            await Assert.That(grab.Success).IsTrue();

            try
            {
                int imported = await acquisition.ImportSoulseekAsync();

                await Assert.That(imported).IsEqualTo(1);
                using ArrkDbContext context = db.CreateDbContext();
                MusicAlbum? album = context.Set<MusicAlbum>().FirstOrDefault(a => a.DeezerId == 999);
                await Assert.That(album).IsNotNull();
                MusicTrack? track = context.Set<MusicTrack>().FirstOrDefault(t => t.AlbumId == album!.Id);
                await Assert.That(track).IsNotNull();
                await Assert.That(track!.HasFile).IsTrue();
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Test]
    public async Task ImportSoulseek_UsesTheRecordedDeezerTrackId_WhenThereIsNoIntent()
    {
        string root = Path.Combine(Path.GetTempPath(), "stackarr-slsk-" + Guid.NewGuid().ToString("N"));
        string soulseek = Path.Combine(root, "downloads", "slskd");
        string library = Path.Combine(root, "library");
        string leaf = Path.Combine(soulseek, "Compilation");
        Directory.CreateDirectory(leaf);
        await File.WriteAllTextAsync(Path.Combine(leaf, "Various - Let It Go.flac"), "a");

        FakeHttpMessageHandler deezer = new();
        deezer.Fallback = (req, _) =>
        {
            string path = req.RequestUri!.AbsolutePath;
            string json = path.Contains("track/500")
                ? """{ "id": 500, "title": "Let It Go (From \"Frozen\"/Soundtrack Version)", "artist": { "id": 7023, "name": "Idina Menzel" }, "album": { "id": 999, "title": "Frozen (Original Motion Picture Soundtrack)" } }"""
                : path.Contains("album/999/tracks")
                    ? """{ "data": [ { "id": 500, "title": "Let It Go (From \"Frozen\"/Soundtrack Version)", "track_position": 1, "disk_number": 1 } ] }"""
                    : """{ "id": 999, "title": "Frozen (Original Motion Picture Soundtrack)", "artist": { "id": 1, "name": "Various Artists" }, "nb_tracks": 1, "record_type": "album" }""";
            return Task.FromResult(FakeHttpMessageHandler.JsonResponse(json));
        };

        (MusicAcquisitionService acquisition, _, TestMusicDb db) = Build(deezerHandler: deezer);
        using (db)
        {
            // The durable grab record carries the Deezer track id (no in-memory intent: as after a
            // restart between grab and import).
            db.Seed(d => d.Set<GrabRecord>().Add(new GrabRecord
            {
                AlbumId = 0,
                Source = "soulseek",
                Identifier = MusicAcquisitionService.SoulseekFileKey("Compilation/Various - Let It Go.flac"),
                ReleaseTitle = "Let It Go",
                DeezerTrackId = 500,
                State = "Grabbed",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            }));
            acquisition.SoulseekRoot = soulseek;
            acquisition.LibraryRoot = library;

            try
            {
                int imported = await acquisition.ImportSoulseekAsync();

                await Assert.That(imported).IsEqualTo(1);
                using ArrkDbContext context = db.CreateDbContext();
                MusicAlbum? album = context.Set<MusicAlbum>().FirstOrDefault(a => a.DeezerId == 999);
                await Assert.That(album).IsNotNull();
                MusicTrack? track = context.Set<MusicTrack>().FirstOrDefault(t => t.AlbumId == album!.Id);
                await Assert.That(track).IsNotNull();
                await Assert.That(track!.HasFile).IsTrue();
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Test]
    public async Task ImportSoulseek_RequestedEndTitle_BindsToTheRequestedSiblingTrack()
    {
        // The Lion King carries two tracks with the same cleaned title: "Can You Feel the Love
        // Tonight" (178s) and "... (End Title)" (243s). A grab for the End Title must land on that
        // exact track. Binding to the first title match rejects every real End Title file as a wrong
        // version, so the request never completes.
        const long plainTrackId = 24307631;
        const long endTitleTrackId = 24307711;

        string root = Path.Combine(Path.GetTempPath(), "stackarr-slsk-" + Guid.NewGuid().ToString("N"));
        string soulseek = Path.Combine(root, "downloads", "slskd");
        string library = Path.Combine(root, "library");
        string leaf = Path.Combine(soulseek, "The Lion King");
        Directory.CreateDirectory(leaf);
        await File.WriteAllTextAsync(Path.Combine(leaf, "12 Can You Feel the Love Tonight.flac"), "a");

        FakeHttpMessageHandler deezer = new();
        deezer.Fallback = (req, _) =>
        {
            string path = req.RequestUri!.AbsolutePath;
            string json = path.Contains("track/24307711")
                ? $$"""{ "id": {{endTitleTrackId}}, "title": "Can You Feel the Love Tonight (End Title)", "artist": { "id": 1, "name": "Elton John" }, "album": { "id": 2323051, "title": "The Lion King" } }"""
                : path.Contains("album/2323051/tracks")
                    ? $$"""{ "data": [ { "id": {{plainTrackId}}, "title": "Can You Feel the Love Tonight", "duration": 178, "track_position": 5, "disk_number": 1 }, { "id": {{endTitleTrackId}}, "title": "Can You Feel the Love Tonight (End Title)", "duration": 243, "track_position": 12, "disk_number": 1 } ] }"""
                    : """{ "id": 2323051, "title": "The Lion King", "artist": { "id": 1, "name": "Various Artists" }, "nb_tracks": 2, "record_type": "album" }""";
            return Task.FromResult(FakeHttpMessageHandler.JsonResponse(json));
        };

        (MusicAcquisitionService acquisition, _, TestMusicDb db) = Build(deezerHandler: deezer);
        using (db)
        {
            db.Seed(d => d.Set<GrabRecord>().Add(new GrabRecord
            {
                AlbumId = 0,
                Source = "soulseek",
                Identifier = MusicAcquisitionService.SoulseekFileKey("The Lion King/12 Can You Feel the Love Tonight.flac"),
                ReleaseTitle = "Can You Feel the Love Tonight (End Title)",
                DeezerTrackId = endTitleTrackId,
                State = "Grabbed",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            }));
            acquisition.SoulseekRoot = soulseek;
            acquisition.LibraryRoot = library;

            try
            {
                int imported = await acquisition.ImportSoulseekAsync();

                await Assert.That(imported).IsEqualTo(1);
                using ArrkDbContext context = db.CreateDbContext();
                MusicAlbum album = context.Set<MusicAlbum>().Single(a => a.DeezerId == 2323051);
                MusicTrack plain = context.Set<MusicTrack>().Single(t => t.AlbumId == album.Id && t.DeezerId == plainTrackId);
                MusicTrack end = context.Set<MusicTrack>().Single(t => t.AlbumId == album.Id && t.DeezerId == endTitleTrackId);
                await Assert.That(end.HasFile).IsTrue();
                await Assert.That(plain.HasFile).IsFalse();
                await Assert.That(end.FilePath).Contains("(End Title)");
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Test]
    public async Task ImportSoulseek_SingleSong_PrefersTheRequestedTracksAlbumOverTheGrabsTitleMatchedAlbum()
    {
        // The enqueue step can resolve an album from the song title alone ("In Too Deep" matching
        // some other release) and record it on the grab. The import must still bind the file to the
        // album the requested Deezer track belongs to; otherwise it is placed under an unrelated track
        // and the requested song is downloaded again.
        string root = Path.Combine(Path.GetTempPath(), "stackarr-slsk-" + Guid.NewGuid().ToString("N"));
        string soulseek = Path.Combine(root, "downloads", "slskd");
        string library = Path.Combine(root, "library");
        string leaf = Path.Combine(soulseek, "Compilation");
        Directory.CreateDirectory(leaf);
        await File.WriteAllTextAsync(Path.Combine(leaf, "Genesis - In Too Deep.flac"), "a");

        FakeHttpMessageHandler deezer = new();
        deezer.Fallback = (req, _) =>
        {
            string path = req.RequestUri!.AbsolutePath;
            string json = path.Contains("track/222")
                ? """{ "id": 222, "title": "In Too Deep", "artist": { "id": 1, "name": "Genesis" }, "album": { "id": 500, "title": "Invisible Touch" } }"""
                : path.Contains("album/500/tracks")
                    ? """{ "data": [ { "id": 111, "title": "Invisible Touch", "track_position": 1, "disk_number": 1 }, { "id": 222, "title": "In Too Deep", "track_position": 2, "disk_number": 1 } ] }"""
                    : """{ "id": 500, "title": "Invisible Touch", "artist": { "id": 1, "name": "Genesis" }, "nb_tracks": 2, "record_type": "album" }""";
            return Task.FromResult(FakeHttpMessageHandler.JsonResponse(json));
        };

        (MusicAcquisitionService acquisition, _, TestMusicDb db) = Build(deezerHandler: deezer);
        using (db)
        {
            // Album 42 is the wrong release the title-based enqueue resolution picked; it isn't even
            // held. The grab still carries the durable requested track id.
            db.Seed(d => d.Set<GrabRecord>().Add(new GrabRecord
            {
                AlbumId = 42,
                Source = "soulseek",
                Identifier = MusicAcquisitionService.SoulseekFileKey("Compilation/Genesis - In Too Deep.flac"),
                ReleaseTitle = "In Too Deep",
                DeezerTrackId = 222,
                State = "Grabbed",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            }));
            acquisition.SoulseekRoot = soulseek;
            acquisition.LibraryRoot = library;

            try
            {
                int imported = await acquisition.ImportSoulseekAsync();

                await Assert.That(imported).IsEqualTo(1);
                using ArrkDbContext context = db.CreateDbContext();
                MusicAlbum album = context.Set<MusicAlbum>().Single(a => a.DeezerId == 500);
                MusicTrack requested = context.Set<MusicTrack>().Single(t => t.AlbumId == album.Id && t.DeezerId == 222);
                MusicTrack other = context.Set<MusicTrack>().Single(t => t.AlbumId == album.Id && t.DeezerId == 111);
                await Assert.That(requested.HasFile).IsTrue();
                await Assert.That(other.HasFile).IsFalse();
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    // Identification from tracks

    [Test]
    public async Task IdentifyAlbum_UsesTrackVotes_WhenTheTitleIsJunk()
    {
        FakeHttpMessageHandler deezer = new();
        deezer.Fallback = (req, _) =>
        {
            string path = req.RequestUri!.AbsolutePath;
            string json = path.Contains("search/album")
                ? """{ "data": [] }"""
                : path.Contains("search/track")
                    ? """{ "data": [ { "id": 1, "title": "Mr. Blue Sky", "artist": { "id": 9, "name": "Electric Light Orchestra" }, "album": { "id": 77, "title": "Out of the Blue" } } ] }"""
                    : path.Contains("/album/77/tracks")
                        ? """{ "data": [ { "id": 100, "title": "Mr. Blue Sky", "track_position": 1, "disk_number": 1 } ] }"""
                        : """{ "id": 77, "title": "Out of the Blue", "artist": { "id": 9, "name": "Electric Light Orchestra" }, "nb_tracks": 1 }""";
            return Task.FromResult(FakeHttpMessageHandler.JsonResponse(json));
        };

        (MusicAcquisitionService acquisition, _, TestMusicDb db) = Build(deezerHandler: deezer);
        using (db)
        {
            MusicItem? identified = await acquisition.IdentifyAlbumAsync("", "CD1", ["Mr. Blue Sky"]);

            await Assert.That(identified).IsNotNull();
            await Assert.That(identified!.DeezerId).IsEqualTo(77);
            await Assert.That(identified.Title).IsEqualTo("Out of the Blue");
        }
    }
}
