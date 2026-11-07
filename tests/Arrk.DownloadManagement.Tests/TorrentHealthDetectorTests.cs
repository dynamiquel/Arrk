using Microsoft.Extensions.Logging.Abstractions;
using Arrk.Core;
using Arrk.Core.Events;
using Arrk.Core.Models;
using Arrk.Core.Services;
using Arrk.Notifications;

using Arrk.TestSupport;
namespace Arrk.DownloadManagement.Tests;

public class TorrentHealthDetectorTests
{
    static (TorrentHealthDetector Detector, FakeTorrentDownloader Torrents, TestTimeProvider Time,
        List<TorrentDeletedEvent> Events, FakeEventStore Store, TorrentPenaltyTracker Tracker, FakeConnectionHealthProvider Health)
        Create(TorrentDownloaderConfig config)
    {
        FakeTorrentDownloader torrents = new();
        EventBus bus = new();
        List<TorrentDeletedEvent> events = [];
        bus.Subscribe<TorrentDeletedEvent>((e, _) =>
        {
            events.Add(e);
            return ValueTask.CompletedTask;
        });
        TestTimeProvider time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        FakeEventStore store = new();
        TorrentPenaltyTracker tracker = new(store, time, NullLogger<TorrentPenaltyTracker>.Instance);
        FakeConnectionHealthProvider health = new();
        TorrentHealthDetector detector = new(
            torrents,
            new MutableOptionsMonitor<TorrentDownloaderConfig>(config),
            health,
            bus,
            store,
            tracker,
            time,
            NullLogger<TorrentHealthDetector>.Instance);
        return (detector, torrents, time, events, store, tracker, health);
    }

    static TorrentItem StalledTorrent(string hash, double progress = 0.5) =>
        TestConfigs.Torrent(hash, progress: progress, downloadSpeed: 0, peers: 0);

    [Test]
    public async Task CheckTorrents_WhenBothDisabled_DoesNotQueryOrDelete()
    {
        (TorrentHealthDetector detector, FakeTorrentDownloader torrents, _, _, _, _, _) =
            Create(TestConfigs.Health(slowEnabled: false, stalledEnabled: false));
        torrents.TorrentsByFilter["all"] = [TestConfigs.Torrent("h1")];

        await detector.CheckTorrents();

        await Assert.That(torrents.FiltersRequested).IsEmpty();
    }

    [Test]
    public async Task CheckTorrents_FirstSlowSighting_DoesNotDelete()
    {
        (TorrentHealthDetector detector, FakeTorrentDownloader torrents, _, List<TorrentDeletedEvent> events, _, _, _) =
            Create(TestConfigs.Slow());
        torrents.TorrentsByFilter["all"] = [TestConfigs.Torrent("h1")];

        await detector.CheckTorrents();

        await Assert.That(torrents.Deleted).IsEmpty();
        await Assert.That(events).IsEmpty();
    }

    [Test]
    public async Task CheckTorrents_SlowBeyondLimit_DeletesAndPublishesEvent()
    {
        (TorrentHealthDetector detector, FakeTorrentDownloader torrents, TestTimeProvider time,
            List<TorrentDeletedEvent> events, _, _, _) = Create(TestConfigs.Slow(limitMinutes: 30));
        torrents.TorrentsByFilter["all"] = [TestConfigs.Torrent("h1")];

        await detector.CheckTorrents();
        time.AdvanceMinutes(31);
        await detector.CheckTorrents();

        await Assert.That(torrents.Deleted.Count).IsEqualTo(1);
        await Assert.That(torrents.Deleted[0].Id).IsEqualTo("h1");
        await Assert.That(torrents.Deleted[0].Cleanup).IsEqualTo(TorrentFileCleanup.DeleteFiles);
        await Assert.That(events.Count).IsEqualTo(1);
        await Assert.That(events[0].TorrentHash).IsEqualTo("h1");
        await Assert.That(events[0].Reason).IsEqualTo(TorrentDeleteReason.Slow);
    }

    [Test]
    public async Task CheckTorrents_SlowWithinLimit_DoesNotDelete()
    {
        (TorrentHealthDetector detector, FakeTorrentDownloader torrents, TestTimeProvider time, _, _, _, _) =
            Create(TestConfigs.Slow(limitMinutes: 30));
        torrents.TorrentsByFilter["all"] = [TestConfigs.Torrent("h1")];

        await detector.CheckTorrents();
        time.AdvanceMinutes(29);
        await detector.CheckTorrents();

        await Assert.That(torrents.Deleted).IsEmpty();
    }

    [Test]
    public async Task CheckTorrents_FastTorrent_IsNeverDeleted()
    {
        (TorrentHealthDetector detector, FakeTorrentDownloader torrents, TestTimeProvider time, _, _, _, _) =
            Create(TestConfigs.Health());
        torrents.TorrentsByFilter["all"] = [TestConfigs.Torrent("h1", downloadSpeed: 5 * 1024 * 1024, peers: 2)];

        await detector.CheckTorrents();
        time.AdvanceMinutes(120);
        await detector.CheckTorrents();

        await Assert.That(torrents.Deleted).IsEmpty();
    }

    [Test]
    public async Task CheckTorrents_StalledBeyondLimit_DeletesWithStalledReason()
    {
        (TorrentHealthDetector detector, FakeTorrentDownloader torrents, TestTimeProvider time,
            List<TorrentDeletedEvent> events, _, _, _) = Create(TestConfigs.Stalled(limitMinutes: 20));
        torrents.TorrentsByFilter["all"] = [StalledTorrent("h1")];

        await detector.CheckTorrents();
        time.AdvanceMinutes(21);
        await detector.CheckTorrents();

        await Assert.That(torrents.Deleted.Count).IsEqualTo(1);
        await Assert.That(events[0].Reason).IsEqualTo(TorrentDeleteReason.Stalled);
    }

    [Test]
    public async Task CheckTorrents_StalledAtFivePercent_ReachesLimitFaster()
    {
        (TorrentHealthDetector detector, FakeTorrentDownloader torrents, TestTimeProvider time, _, _, _, _) =
            Create(TestConfigs.Stalled(limitMinutes: 20, zeroProgressMultiplier: 4));
        torrents.TorrentsByFilter["all"] = [StalledTorrent("h1", progress: 0.04)];

        await detector.CheckTorrents();
        time.AdvanceMinutes(4);
        await detector.CheckTorrents();
        await Assert.That(torrents.Deleted).IsEmpty();

        time.AdvanceMinutes(1);
        await detector.CheckTorrents();
        await Assert.That(torrents.Deleted.Count).IsEqualTo(1);
    }

    [Test]
    public async Task CheckTorrents_StalledAboveFivePercent_AccruesAtNormalRate()
    {
        (TorrentHealthDetector detector, FakeTorrentDownloader torrents, TestTimeProvider time,
            _, _, TorrentPenaltyTracker tracker, _) =
            Create(TestConfigs.Stalled(limitMinutes: 20, zeroProgressMultiplier: 4));
        torrents.TorrentsByFilter["all"] = [StalledTorrent("h1", progress: 0.1)];

        await detector.CheckTorrents();
        time.AdvanceMinutes(10);
        await detector.CheckTorrents();

        await Assert.That(tracker.GetPenalties()[0].PenaltyMinutes).IsEqualTo(10);
        await Assert.That(torrents.Deleted).IsEmpty();
    }

    [Test]
    public async Task CheckTorrents_HealthyAgain_DecaysAndRecovers()
    {
        (TorrentHealthDetector detector, FakeTorrentDownloader torrents, TestTimeProvider time,
            _, _, TorrentPenaltyTracker tracker, _) = Create(TestConfigs.Slow(limitMinutes: 30));

        torrents.TorrentsByFilter["all"] = [TestConfigs.Torrent("h1")];
        await detector.CheckTorrents();
        time.AdvanceMinutes(10);
        await detector.CheckTorrents();

        await Assert.That(tracker.GetPenalties()[0].PenaltyMinutes).IsEqualTo(10);

        // Healthy for 12 minutes removes 6 penalty minutes.
        torrents.TorrentsByFilter["all"] = [TestConfigs.Torrent("h1", downloadSpeed: 10 * 1024 * 1024, peers: 4)];
        time.AdvanceMinutes(12);
        await detector.CheckTorrents();

        await Assert.That(tracker.GetPenalties()[0].PenaltyMinutes).IsEqualTo(4);
    }

    [Test]
    public async Task CheckTorrents_HealthyLongEnough_DropsPenalty()
    {
        (TorrentHealthDetector detector, FakeTorrentDownloader torrents, TestTimeProvider time,
            _, _, TorrentPenaltyTracker tracker, _) = Create(TestConfigs.Slow(limitMinutes: 30));

        torrents.TorrentsByFilter["all"] = [TestConfigs.Torrent("h1")];
        await detector.CheckTorrents();
        time.AdvanceMinutes(10);
        await detector.CheckTorrents();

        torrents.TorrentsByFilter["all"] = [TestConfigs.Torrent("h1", downloadSpeed: 10 * 1024 * 1024, peers: 4)];
        time.AdvanceMinutes(40);
        await detector.CheckTorrents();

        await Assert.That(tracker.GetPenalties()).IsEmpty();
    }

    [Test]
    public async Task CheckTorrents_CompleteOrErrored_AreExcluded()
    {
        (TorrentHealthDetector detector, FakeTorrentDownloader torrents, TestTimeProvider time, _, _, _, _) =
            Create(TestConfigs.Health(slowLimitMinutes: 1, stalledLimitMinutes: 1));
        torrents.TorrentsByFilter["all"] =
        [
            TestConfigs.Torrent("done", state: TorrentState.Seeding, progress: 1.0),
            TestConfigs.Torrent("error", state: TorrentState.Error, progress: 0.2),
            TestConfigs.Torrent("missing", state: TorrentState.MissingFiles, progress: 0.2)
        ];

        await detector.CheckTorrents();
        time.AdvanceMinutes(5);
        await detector.CheckTorrents();

        await Assert.That(torrents.Deleted).IsEmpty();
    }

    [Test]
    public async Task CheckTorrents_PausedQueuedAndUnknown_AreNotTracked()
    {
        (TorrentHealthDetector detector, FakeTorrentDownloader torrents, TestTimeProvider time,
            _, _, TorrentPenaltyTracker tracker, _) = Create(TestConfigs.Health(slowLimitMinutes: 1, stalledLimitMinutes: 1));
        torrents.TorrentsByFilter["all"] =
        [
            TestConfigs.Torrent("paused", state: TorrentState.Paused, progress: 0.05),
            TestConfigs.Torrent("queued", state: TorrentState.Queued, progress: 0.3),
            TestConfigs.Torrent("unknown", state: TorrentState.Unknown, progress: 0.3)
        ];

        await detector.CheckTorrents();
        time.AdvanceMinutes(2);
        await detector.CheckTorrents();

        await Assert.That(torrents.Deleted).IsEmpty();
        await Assert.That(tracker.GetPenalties()).IsEmpty();
    }

    [Test]
    public async Task CheckTorrents_UnhealthyConnection_DoesNotAccrueOrDelete()
    {
        (TorrentHealthDetector detector, FakeTorrentDownloader torrents, TestTimeProvider time,
            _, _, TorrentPenaltyTracker tracker, FakeConnectionHealthProvider health) = Create(TestConfigs.Slow(limitMinutes: 30));
        torrents.TorrentsByFilter["all"] = [TestConfigs.Torrent("h1")];

        health.Health = new ConnectionHealth { IsHealthy = false, IsKnown = true, Target = "network", DownloadMbps = 1 };
        await detector.CheckTorrents();
        time.AdvanceMinutes(30);
        await detector.CheckTorrents();

        await Assert.That(torrents.Deleted).IsEmpty();
        await Assert.That(tracker.GetPenalties()).IsEmpty();
    }

    [Test]
    public async Task CheckTorrents_ConnectionRecovers_DowntimeIsNotCounted()
    {
        (TorrentHealthDetector detector, FakeTorrentDownloader torrents, TestTimeProvider time,
            _, _, TorrentPenaltyTracker tracker, FakeConnectionHealthProvider health) = Create(TestConfigs.Slow(limitMinutes: 30));
        torrents.TorrentsByFilter["all"] = [TestConfigs.Torrent("h1")];

        // Accrue 10 penalty minutes before the outage.
        await detector.CheckTorrents();
        time.AdvanceMinutes(10);
        await detector.CheckTorrents();

        // A 30 minute outage freezes the clock.
        health.Health = new ConnectionHealth { IsHealthy = false, IsKnown = true, Target = "network" };
        time.AdvanceMinutes(30);
        await detector.CheckTorrents();
        await Assert.That(tracker.GetPenalties()[0].PenaltyMinutes).IsEqualTo(10);

        // Only the minute after recovery counts.
        health.Health = new ConnectionHealth { IsHealthy = true, IsKnown = true, Target = "network" };
        time.AdvanceMinutes(1);
        await detector.CheckTorrents();

        await Assert.That(tracker.GetPenalties()[0].PenaltyMinutes).IsEqualTo(11);
    }

    [Test]
    public async Task CheckTorrents_SlowDisabled_StalledStillCleanedUp()
    {
        (TorrentHealthDetector detector, FakeTorrentDownloader torrents, TestTimeProvider time, _, _, _, _) =
            Create(TestConfigs.Health(slowEnabled: false, stalledEnabled: true, stalledLimitMinutes: 1));
        torrents.TorrentsByFilter["all"] = [StalledTorrent("h1")];

        await detector.CheckTorrents();
        time.AdvanceMinutes(2);
        await detector.CheckTorrents();

        await Assert.That(torrents.Deleted.Count).IsEqualTo(1);
    }

    [Test]
    public async Task CheckTorrents_StalledDisabled_FallsBackToSlow()
    {
        (TorrentHealthDetector detector, FakeTorrentDownloader torrents, TestTimeProvider time,
            List<TorrentDeletedEvent> events, _, _, _) =
            Create(TestConfigs.Health(slowEnabled: true, stalledEnabled: false, slowLimitMinutes: 1));
        torrents.TorrentsByFilter["all"] = [StalledTorrent("h1")];

        await detector.CheckTorrents();
        time.AdvanceMinutes(2);
        await detector.CheckTorrents();

        await Assert.That(torrents.Deleted.Count).IsEqualTo(1);
        await Assert.That(events[0].Reason).IsEqualTo(TorrentDeleteReason.Slow);
    }

    [Test]
    public async Task CheckTorrents_ZeroLimit_IsClampedToEndQuickly()
    {
        (TorrentHealthDetector detector, FakeTorrentDownloader torrents, TestTimeProvider time, _, _, _, _) =
            Create(TestConfigs.Slow(limitMinutes: 0));
        torrents.TorrentsByFilter["all"] = [TestConfigs.Torrent("h1")];

        await detector.CheckTorrents();
        time.Advance(TimeSpan.FromSeconds(30));
        await detector.CheckTorrents();
        await Assert.That(torrents.Deleted).IsEmpty();

        time.Advance(TimeSpan.FromSeconds(31));
        await detector.CheckTorrents();
        await Assert.That(torrents.Deleted.Count).IsEqualTo(1);
    }

    [Test]
    public async Task CheckTorrents_TorrentDisappears_StateIsPruned()
    {
        (TorrentHealthDetector detector, FakeTorrentDownloader torrents, TestTimeProvider time,
            _, _, TorrentPenaltyTracker tracker, _) = Create(TestConfigs.Slow(limitMinutes: 30));

        torrents.TorrentsByFilter["all"] = [TestConfigs.Torrent("h1")];
        await detector.CheckTorrents();
        time.AdvanceMinutes(10);
        await detector.CheckTorrents();
        await Assert.That(tracker.GetPenalties().Count).IsEqualTo(1);

        torrents.TorrentsByFilter["all"] = [];
        await detector.CheckTorrents();

        await Assert.That(tracker.GetPenalties()).IsEmpty();
    }

    [Test]
    public async Task CheckTorrents_ConcurrentRuns_DeleteExactlyOnce()
    {
        (TorrentHealthDetector detector, FakeTorrentDownloader torrents, TestTimeProvider time,
            List<TorrentDeletedEvent> events, _, _, _) = Create(TestConfigs.Slow(limitMinutes: 30));
        torrents.TorrentsByFilter["all"] = [TestConfigs.Torrent("h1")];

        await detector.CheckTorrents();
        time.AdvanceMinutes(31);

        IEnumerable<Task> runs = Enumerable.Range(0, 25).Select(_ => detector.CheckTorrents());
        await Task.WhenAll(runs);

        await Assert.That(torrents.Deleted.Count).IsEqualTo(1);
        await Assert.That(events.Count).IsEqualTo(1);
    }

    [Test]
    public async Task CheckTorrents_SlowSighting_AttachesSlowWarningNotification()
    {
        (TorrentHealthDetector detector, FakeTorrentDownloader torrents, _, _, FakeEventStore store, _, _) =
            Create(TestConfigs.Slow());
        torrents.TorrentsByFilter["all"] = [TestConfigs.Torrent("h1")];

        await detector.CheckTorrents();

        NotificationMessage notification = store.Notifications.Single();
        await Assert.That(notification.Key).IsEqualTo(DownloadNotificationTopics.TorrentSlow);
        await Assert.That(notification.Severity).IsEqualTo(EventSeverity.Warning);
    }

    [Test]
    public async Task CheckTorrents_Deletion_AttachesStruckNotification()
    {
        (TorrentHealthDetector detector, FakeTorrentDownloader torrents, TestTimeProvider time, _, FakeEventStore store, _, _) =
            Create(TestConfigs.Slow(limitMinutes: 30));
        torrents.TorrentsByFilter["all"] = [TestConfigs.Torrent("h1")];

        await detector.CheckTorrents();
        time.AdvanceMinutes(31);
        await detector.CheckTorrents();

        await Assert.That(store.Notifications.Any(n => n.Key == DownloadNotificationTopics.TorrentStriked)).IsTrue();
    }

    [Test]
    public async Task CheckTorrents_CancellationDuringQuery_Propagates()
    {
        (TorrentHealthDetector detector, FakeTorrentDownloader torrents, _, _, _, _, _) = Create(TestConfigs.Slow());
        torrents.GetTorrentsException = new OperationCanceledException();

        await Assert.That(async () => await detector.CheckTorrents())
            .Throws<OperationCanceledException>();
    }
}
