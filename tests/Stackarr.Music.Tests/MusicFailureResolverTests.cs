using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.Extensions.Logging.Abstractions;
using Stackarr.Core.Events;
using Stackarr.Music.Jobs;
using Stackarr.Music.Models;
using Stackarr.Music.Services;

namespace Stackarr.Music.Tests;

public class MusicFailureResolverTests
{
    sealed class RecordingBackgroundJobClient : IBackgroundJobClient
    {
        public List<Job> Created { get; } = [];

        public string Create(Job job, IState state)
        {
            Created.Add(job);
            return Guid.NewGuid().ToString();
        }

        public bool ChangeState(string jobId, IState state, string expectedState) => true;
    }

    static (MusicFailureResolver Resolver, EventBus Bus, RecordingBackgroundJobClient Jobs, TestMusicDb Db) Create()
    {
        EventBus bus = new();
        RecordingBackgroundJobClient jobs = new();
        TestMusicDb db = new();
        db.Seed(d => d.Set<GrabRecord>().Add(new GrabRecord
        {
            AlbumId = 42,
            Source = "torrent",
            Identifier = "hash1",
            ReleaseTitle = "Some Album [FLAC]",
            State = "Grabbed"
        }));
        MusicLibrary library = MusicTestHelpers.Library(db);
        MusicAcquisitionService acquisition = MusicTestHelpers.Acquisition(
            library, db, MusicTestHelpers.EmptyHandler(), MusicTestHelpers.EmptyHandler());
        MusicFailureResolver resolver = new(bus, jobs, db, library, acquisition, NullLogger<MusicFailureResolver>.Instance);
        return (resolver, bus, jobs, db);
    }

    static TorrentDeletedEvent MusicEvent(TorrentDeleteReason reason) => new(
        "hash1",
        "Some Album [FLAC]",
        reason,
        DateTime.UtcNow,
        "music",
        [MusicAcquisitionService.DownloadLabel]);

    [Test]
    public async Task SeedingCompleted_DoesNotReacquire()
    {
        (MusicFailureResolver resolver, EventBus bus, RecordingBackgroundJobClient jobs, TestMusicDb db) = Create();
        using (resolver)
        using (db)
        {
            await bus.Publish(MusicEvent(TorrentDeleteReason.SeedingCompleted));
        }

        await Assert.That(jobs.Created).IsEmpty();
    }

    [Test]
    public async Task Storage_DoesNotReacquire()
    {
        (MusicFailureResolver resolver, EventBus bus, RecordingBackgroundJobClient jobs, TestMusicDb db) = Create();
        using (resolver)
        using (db)
        {
            await bus.Publish(MusicEvent(TorrentDeleteReason.Storage));
        }

        await Assert.That(jobs.Created).IsEmpty();
    }

    [Test]
    public async Task Failure_Reacquires()
    {
        (MusicFailureResolver resolver, EventBus bus, RecordingBackgroundJobClient jobs, TestMusicDb db) = Create();
        using (resolver)
        using (db)
        {
            await bus.Publish(MusicEvent(TorrentDeleteReason.Stalled));
        }

        await Assert.That(jobs.Created.Count).IsEqualTo(1);
        await Assert.That(jobs.Created[0].Type).IsEqualTo(typeof(MusicAcquireQueueJob));
    }
}
