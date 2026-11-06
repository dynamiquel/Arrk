using Microsoft.Extensions.Logging.Abstractions;
using Arrk.Core;
using Arrk.Music.Jobs;
using Arrk.Music.Models;
using Arrk.Music.Services;
using Arrk.Slskd;
using Arrk.TestSupport;

namespace Arrk.Music.Tests;

/// <summary>
/// Covers Arrk's music acquisition queue: one persisted row per request, drained in small batches,
/// ordered so the requests that have failed the most go last, with the drain job advancing each item
/// (in-flight on a grab, one more attempt on a miss).
/// </summary>
public class MusicAcquireQueueTests
{
    static (MusicLibrary Library, MusicAcquisitionService Acquisition, SlskdService Slskd, TestMusicDb Db) Build(
        FakeHttpMessageHandler? slskdHandler = null, bool slskdEnabled = false)
    {
        TestMusicDb db = new();
        MusicLibrary library = MusicTestHelpers.Library(db);
        SlskdService slskd = MusicTestHelpers.Slskd(slskdHandler, slskdEnabled);
        MusicAcquisitionService acquisition = MusicTestHelpers.Acquisition(
            library, db, MusicTestHelpers.EmptyHandler(), MusicTestHelpers.EmptyHandler(),
            slskdHandler ?? MusicTestHelpers.EmptyHandler(), slskdEnabled);
        return (library, acquisition, slskd, db);
    }

    static MusicAcquireQueueJob Job(
        MusicLibrary library, MusicAcquisitionService acquisition, SlskdService slskd) =>
        new(library, acquisition, slskd, NullLogger<MusicAcquireQueueJob>.Instance);

    static FakeHttpMessageHandler SlskdHandler(bool loggedIn, Func<HttpRequestMessage, string>? search = null)
    {
        FakeHttpMessageHandler handler = new();
        handler.Fallback = (req, _) =>
        {
            string path = req.RequestUri!.AbsolutePath;
            string json = path.Contains("/session")
                ? """{ "token": "t" }"""
                : path.EndsWith("/server")
                    ? $$"""{ "isLoggedIn": {{(loggedIn ? "true" : "false")}} }"""
                    : path.Contains("/searches")
                        ? search?.Invoke(req) ?? """{ "id": "s1", "isComplete": true, "responses": [] }"""
                        : path.Contains("/transfers/downloads/")
                            ? """{ "ok": true }"""
                            : "[]";
            return Task.FromResult(FakeHttpMessageHandler.JsonResponse(json));
        };
        return handler;
    }

    [Test]
    public async Task Enqueue_IsIdempotent_AndTakeBatch_OrdersTheLeastFailedFirst()
    {
        (MusicLibrary library, _, _, TestMusicDb db) = Build();
        using (db)
        {
            await library.EnqueueAcquireAsync("A", "Song A", 0, 11, true);
            await library.EnqueueAcquireAsync("B", "Song B", 0, 22, true);
            await library.EnqueueAcquireAsync("C", "Song C", 0, 33, true);
            await library.EnqueueAcquireAsync("A", "Song A", 0, 11, true); // duplicate — ignored

            // C has failed twice; A and B are fresh.
            await library.RequeueAcquireAsync("C", "Song C", 0, 33, true, "wrong version");
            await library.RequeueAcquireAsync("C", "Song C", 0, 33, true, "wrong version");

            List<MusicAcquireQueueItem> batch = await library.TakeAcquireBatchAsync(10);

            await Assert.That(batch.Count).IsEqualTo(3);
            await Assert.That(batch[0].Title).IsEqualTo("Song A");
            await Assert.That(batch[1].Title).IsEqualTo("Song B");
            await Assert.That(batch[2].Title).IsEqualTo("Song C");   // most failed -> last
            await Assert.That(batch[2].Attempts).IsEqualTo(2);
        }
    }

    [Test]
    public async Task TakeBatch_ReclaimsAStaleInFlightRequest_ButSkipsARecentOne()
    {
        (MusicLibrary library, _, _, TestMusicDb db) = Build();
        using (db)
        {
            await library.EnqueueAcquireAsync("A", "Song A", 0, 11, true);
            await library.EnqueueAcquireAsync("B", "Song B", 0, 22, true);
            using (ArrkDbContext ctx = db.CreateDbContext())
            {
                MusicAcquireQueueItem stale = ctx.Set<MusicAcquireQueueItem>().Single(q => q.Title == "Song A");
                stale.InFlight = true;
                stale.LastAttemptAt = DateTime.UtcNow.AddDays(-2);
                MusicAcquireQueueItem fresh = ctx.Set<MusicAcquireQueueItem>().Single(q => q.Title == "Song B");
                fresh.InFlight = true;
                fresh.LastAttemptAt = DateTime.UtcNow;
                await ctx.SaveChangesAsync();
            }

            List<MusicAcquireQueueItem> batch = await library.TakeAcquireBatchAsync(10);

            await Assert.That(batch.Count).IsEqualTo(1);
            await Assert.That(batch[0].Title).IsEqualTo("Song A");
        }
    }

    [Test]
    public async Task DrainJob_IncrementsAttempts_WhenTheSearchGrabsNothing()
    {
        // Soulseek disabled and no torrents configured → every attempt misses.
        (MusicLibrary library, MusicAcquisitionService acquisition, SlskdService slskd, TestMusicDb db) = Build();
        using (db)
        {
            await library.EnqueueAcquireAsync("Idina Menzel", "Let It Go", 0, 72371930, true);
            MusicAcquireQueueJob job = Job(library, acquisition, slskd);

            await job.Execute(CancellationToken.None);

            using ArrkDbContext ctx = db.CreateDbContext();
            MusicAcquireQueueItem item = ctx.Set<MusicAcquireQueueItem>().Single();
            await Assert.That(item.Attempts).IsEqualTo(1);
            await Assert.That(item.InFlight).IsFalse();
        }
    }

    [Test]
    public async Task DrainJob_MarksInFlight_WhenItGrabsSomething()
    {
        FakeHttpMessageHandler slskd = SlskdHandler(loggedIn: true, _ => """
            { "id": "s1", "isComplete": true, "responses": [
              { "username": "peer1", "hasFreeUploadSlot": true, "queueLength": 0, "files": [
                { "filename": "Music\\Idina Menzel - Let It Go.flac", "size": 30000000, "bitRate": 0 } ] } ] }
            """);

        (MusicLibrary library, MusicAcquisitionService acquisition, SlskdService slskdService, TestMusicDb db) =
            Build(slskd, slskdEnabled: true);
        using (db)
        {
            await library.EnqueueAcquireAsync("Idina Menzel", "Let It Go", 0, 72371930, true);
            MusicAcquireQueueJob job = Job(library, acquisition, slskdService);

            await job.Execute(CancellationToken.None);

            using ArrkDbContext ctx = db.CreateDbContext();
            MusicAcquireQueueItem item = ctx.Set<MusicAcquireQueueItem>().Single();
            await Assert.That(item.InFlight).IsTrue();
            await Assert.That(item.Attempts).IsEqualTo(0);
        }
    }

    [Test]
    public async Task DrainJob_SkipsWhileSoulseekIsUnavailable()
    {
        // slskd logged out (Soulseek dropped us): the run must not mark every request as failed.
        (MusicLibrary library, MusicAcquisitionService acquisition, SlskdService slskd, TestMusicDb db) =
            Build(SlskdHandler(loggedIn: false), slskdEnabled: true);
        using (db)
        {
            await library.EnqueueAcquireAsync("Idina Menzel", "Let It Go", 0, 72371930, true);
            MusicAcquireQueueJob job = Job(library, acquisition, slskd);

            await job.Execute(CancellationToken.None);

            using ArrkDbContext ctx = db.CreateDbContext();
            await Assert.That(ctx.Set<MusicAcquireQueueItem>().Single().Attempts).IsEqualTo(0);
        }
    }

    [Test]
    public async Task DrainJob_CountsATimedOutSearch_AsAnAttempt()
    {
        // A search that never settles must not hold a slot forever; it times out and drops down the queue.
        FakeHttpMessageHandler slskd = SlskdHandler(loggedIn: true, _ => """{ "id": "s1", "isComplete": false, "responses": [] }""");
        (MusicLibrary library, MusicAcquisitionService acquisition, SlskdService slskdService, TestMusicDb db) =
            Build(slskd, slskdEnabled: true);
        using (db)
        {
            await library.EnqueueAcquireAsync("Idina Menzel", "Let It Go", 0, 72371930, true);
            MusicAcquireQueueJob job = Job(library, acquisition, slskdService);
            job.ItemTimeout = TimeSpan.FromMilliseconds(50);

            await job.Execute(CancellationToken.None);

            using ArrkDbContext ctx = db.CreateDbContext();
            MusicAcquireQueueItem item = ctx.Set<MusicAcquireQueueItem>().Single();
            await Assert.That(item.Attempts).IsEqualTo(1);
            await Assert.That(item.LastError).Contains("timed out");
        }
    }

    [Test]
    public async Task CompleteAcquire_RemovesTheRequest()
    {
        (MusicLibrary library, _, _, TestMusicDb db) = Build();
        using (db)
        {
            await library.EnqueueAcquireAsync("Idina Menzel", "Let It Go", 0, 72371930, true);

            await library.CompleteAcquireAsync(albumId: 0, deezerTrackId: 72371930);

            await Assert.That(await library.CountAcquireQueueAsync()).IsEqualTo(0);
        }
    }

    [Test]
    public async Task Summary_ReportsPendingInFlightAndRetrying()
    {
        (MusicLibrary library, _, _, TestMusicDb db) = Build();
        using (db)
        {
            await library.EnqueueAcquireAsync("A", "Song A", 0, 11, true);
            await library.EnqueueAcquireAsync("B", "Song B", 0, 22, true);
            await library.EnqueueAcquireAsync("C", "Song C", 0, 33, true);
            await library.RequeueAcquireAsync("C", "Song C", 0, 33, true, "miss");

            List<MusicAcquireQueueItem> batch = await library.TakeAcquireBatchAsync(1);
            await library.MarkAcquireInFlightAsync(batch[0].Id);

            (int total, int inFlight, int retrying) = await library.GetAcquireQueueSummaryAsync();

            await Assert.That(total).IsEqualTo(3);
            await Assert.That(inFlight).IsEqualTo(1);
            await Assert.That(retrying).IsEqualTo(1);
        }
    }

    [Test]
    public async Task TakeBatch_ParksARequestAtTheAttemptCap_AndAnExplicitRearmRevivesIt()
    {
        (MusicLibrary library, _, _, TestMusicDb db) = Build();
        using (db)
        {
            await library.EnqueueAcquireAsync("Idina Menzel", "Let It Go", 0, 72371930, true);

            using (ArrkDbContext ctx = db.CreateDbContext())
            {
                MusicAcquireQueueItem item = ctx.Set<MusicAcquireQueueItem>().Single();
                item.Attempts = MusicAcquireQueueItem.MaxAttempts;
                await ctx.SaveChangesAsync();
            }

            // Parked: the drain job must not pick it up again.
            await Assert.That(await library.TakeAcquireBatchAsync(10)).IsEmpty();

            // An automatic re-enqueue (a subscription sync) must not silently defeat the cap.
            await library.EnqueueAcquireAsync("Idina Menzel", "Let It Go", 0, 72371930, true);
            await Assert.That(await library.TakeAcquireBatchAsync(10)).IsEmpty();

            // An explicit search re-arms it with a fresh set of attempts.
            await library.RearmAcquireAsync("Idina Menzel", "Let It Go", 0, 72371930);

            List<MusicAcquireQueueItem> batch = await library.TakeAcquireBatchAsync(10);
            await Assert.That(batch.Count).IsEqualTo(1);
            await Assert.That(batch[0].Attempts).IsEqualTo(0);
        }
    }

    [Test]
    public async Task RecordFailure_ParksTheRequestWhenItReachesTheCap()
    {
        (MusicLibrary library, _, _, TestMusicDb db) = Build();
        using (db)
        {
            await library.EnqueueAcquireAsync("Idina Menzel", "Let It Go", 0, 72371930, true);
            int id;
            using (ArrkDbContext ctx = db.CreateDbContext())
            {
                MusicAcquireQueueItem item = ctx.Set<MusicAcquireQueueItem>().Single();
                item.Attempts = MusicAcquireQueueItem.MaxAttempts - 1;
                id = item.Id;
                await ctx.SaveChangesAsync();
            }

            await library.RecordAcquireFailureAsync(id, "no release found");

            using (ArrkDbContext ctx = db.CreateDbContext())
            {
                MusicAcquireQueueItem item = ctx.Set<MusicAcquireQueueItem>().Single();
                await Assert.That(item.Attempts).IsEqualTo(MusicAcquireQueueItem.MaxAttempts);
                await Assert.That(item.LastError).IsEqualTo("no release found");
            }
            await Assert.That(await library.TakeAcquireBatchAsync(10)).IsEmpty();
        }
    }

    [Test]
    public async Task DrainJob_DoesNotTouchAParkedRequest()
    {
        (MusicLibrary library, MusicAcquisitionService acquisition, SlskdService slskd, TestMusicDb db) = Build();
        using (db)
        {
            await library.EnqueueAcquireAsync("Idina Menzel", "Let It Go", 0, 72371930, true);
            using (ArrkDbContext ctx = db.CreateDbContext())
            {
                MusicAcquireQueueItem item = ctx.Set<MusicAcquireQueueItem>().Single();
                item.Attempts = MusicAcquireQueueItem.MaxAttempts;
                await ctx.SaveChangesAsync();
            }

            await Job(library, acquisition, slskd).Execute(CancellationToken.None);

            using (ArrkDbContext ctx = db.CreateDbContext())
                await Assert.That(ctx.Set<MusicAcquireQueueItem>().Single().Attempts)
                    .IsEqualTo(MusicAcquireQueueItem.MaxAttempts);
        }
    }

    [Test]
    public async Task DrainJob_DropsARequestTheLibraryAlreadyHolds_InsteadOfDownloadingItAgain()
    {
        // The playlist asks for the original-edition track id and title while the library already
        // holds the 2007 remaster of the same song. A row can be re-added before Navidrome rescans, so
        // the drain must skip a song the library already holds rather than download another copy.
        (MusicLibrary library, MusicAcquisitionService acquisition, SlskdService slskd, TestMusicDb db) = Build();
        using (db)
        {
            db.Seed(d =>
            {
                MusicArtist artist = new() { Name = "Genesis", SortName = "Genesis", Added = DateTime.UtcNow };
                MusicAlbum album = new() { Title = "Invisible Touch (2007 Remaster)", Monitored = false, TotalTrackCount = 2 };
                album.Tracks.Add(new MusicTrack
                {
                    Title = "Invisible Touch (2007 Remaster)", TrackNumber = 1, AbsoluteTrackNumber = 1,
                    DeezerId = 14637100, HasFile = true, FilePath = "Genesis/Invisible Touch (2007 Remaster)/01.flac"
                });
                album.Tracks.Add(new MusicTrack
                {
                    Title = "Land of Confusion (2007 Remaster)", TrackNumber = 3, AbsoluteTrackNumber = 3,
                    DeezerId = 14637102, HasFile = true, FilePath = "Genesis/Invisible Touch (2007 Remaster)/03.flac"
                });
                artist.Albums.Add(album);
                d.Set<MusicArtist>().Add(artist);
            });
            await library.EnqueueAcquireAsync("Genesis", "Invisible Touch (Remastered 2007)", 0, 3145739, true);

            await Job(library, acquisition, slskd).Execute(CancellationToken.None);

            await Assert.That(await library.CountAcquireQueueAsync()).IsEqualTo(0);
        }
    }
}
