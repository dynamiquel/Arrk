using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.Extensions.Logging.Abstractions;
using Stackarr.Core;
using Stackarr.Music.Models;
using Stackarr.Music.Services;
using Stackarr.TestSupport;

namespace Stackarr.Music.Tests;

/// <summary>
/// Covers the per-song actions behind the album popup's "..." menu: deleting a placed file clears it
/// from the library, and a force re-search drops the current file before queueing a fresh grab.
/// </summary>
public class MusicTrackActionsTests
{
    sealed class RecordingBackgroundJobClient : IBackgroundJobClient
    {
        public List<Job> Created { get; } = [];
        public string Create(Job job, IState state) { Created.Add(job); return Guid.NewGuid().ToString(); }
        public bool ChangeState(string jobId, IState state, string expectedState) => true;
    }

    static (MusicCatalogService Catalog, MusicAcquisitionService Acquisition, RecordingBackgroundJobClient Jobs, TestMusicDb Db) Build()
    {
        TestMusicDb db = new();
        MusicLibrary library = MusicTestHelpers.Library(db);
        FakeHttpMessageHandler deezerHandler = new();
        deezerHandler.Fallback = (_, _) => Task.FromResult(FakeHttpMessageHandler.JsonResponse("{}"));
        RecordingBackgroundJobClient jobs = new();
        MusicAcquisitionService acquisition = MusicTestHelpers.Acquisition(
            library, db, MusicTestHelpers.EmptyHandler(), MusicTestHelpers.EmptyHandler(),
            backgroundJobs: jobs, deezerHandler: deezerHandler);
        MusicCatalogService catalog = new(
            new DeezerService(NullLogger<DeezerService>.Instance, deezerHandler),
            library, acquisition, NullLogger<MusicCatalogService>.Instance, backgroundJobs: jobs);
        return (catalog, acquisition, jobs, db);
    }

    static (int AlbumId, int TrackId, string Root, string Rel) SeedPlacedTrack(TestMusicDb db, MusicAcquisitionService acquisition)
    {
        string root = Path.Combine(Path.GetTempPath(), "stackarr-track-" + Guid.NewGuid().ToString("N"));
        string rel = "Various Artists/Frozen (Original Motion Picture Soundtrack)/05 - Let It Go.flac";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, rel))!);
        File.WriteAllText(Path.Combine(root, rel), "audio");
        acquisition.LibraryRoot = root;

        int albumId = 0;
        int trackId = 0;
        db.Seed(d =>
        {
            MusicArtist artist = new() { Name = "Various Artists", Added = DateTime.UtcNow };
            MusicAlbum album = new() { Title = "Frozen (Original Motion Picture Soundtrack)", DeezerId = 7113713, TotalTrackCount = 1, Added = DateTime.UtcNow };
            MusicTrack track = new()
            {
                Title = "Let It Go",
                TrackNumber = 5,
                AbsoluteTrackNumber = 5,
                DeezerId = 72371930,
                HasFile = true,
                FilePath = rel
            };
            album.Tracks.Add(track);
            artist.Albums.Add(album);
            d.Set<MusicArtist>().Add(artist);
            d.SaveChanges();
            albumId = album.Id;
            trackId = track.Id;
        });
        return (albumId, trackId, root, rel);
    }

    [Test]
    public async Task DeleteTrackFile_RemovesTheFileAndClearsTheTrack()
    {
        (MusicCatalogService catalog, MusicAcquisitionService acquisition, _, TestMusicDb db) = Build();
        using (db)
        {
            (int albumId, int trackId, string root, string rel) = SeedPlacedTrack(db, acquisition);
            try
            {
                bool deleted = await catalog.DeleteTrackFileAsync(albumId, trackId);

                await Assert.That(deleted).IsTrue();
                await Assert.That(File.Exists(Path.Combine(root, rel))).IsFalse();
                using StackarrDbContext ctx = db.CreateDbContext();
                await Assert.That(ctx.Set<MusicTrack>().Single(t => t.Id == trackId).HasFile).IsFalse();
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }
    }

    [Test]
    public async Task ResearchTrack_DropsTheFileAndQueuesANewGrab()
    {
        (MusicCatalogService catalog, MusicAcquisitionService acquisition, RecordingBackgroundJobClient jobs, TestMusicDb db) = Build();
        using (db)
        {
            (int albumId, int trackId, string root, string rel) = SeedPlacedTrack(db, acquisition);
            try
            {
                await catalog.ResearchTrackAsync("Various Artists", "Let It Go", 72371930);

                await Assert.That(File.Exists(Path.Combine(root, rel))).IsFalse();
                await Assert.That(jobs.Created.Count).IsEqualTo(1);
                await Assert.That(jobs.Created[0].Type).IsEqualTo(typeof(Jobs.MusicAcquireQueueJob));
                using StackarrDbContext ctx = db.CreateDbContext();
                await Assert.That(ctx.Set<MusicAcquireQueueItem>().Any(i => i.Key == "track:72371930")).IsTrue();
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }
    }
}
