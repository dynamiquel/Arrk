using Microsoft.Extensions.Logging.Abstractions;
using Arrk.Core;
using Arrk.Core.Events;
using Arrk.Core.Models;
using Arrk.Notifications;
using Arrk.Core.Services;

using Arrk.TestSupport;
namespace Arrk.DownloadManagement.Tests;

public class SeedingLimitCleanupTests
{
    static (SeedingLimitCleanup Cleanup, FakeTorrentDownloader Torrents, List<TorrentDeletedEvent> Events, FakeEventStore Store)
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
        FakeEventStore store = new();
        SeedingLimitCleanup cleanup = new(
            torrents,
            new MutableOptionsMonitor<TorrentDownloaderConfig>(config),
            bus,
            store,
            new TestTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            NullLogger<SeedingLimitCleanup>.Instance);
        return (cleanup, torrents, events, store);
    }

    static TorrentItem Finished(string hash, double ratio, long secondsSeeding) =>
        TestConfigs.Torrent(hash, state: TorrentState.Seeding, progress: 1.0, ratio: ratio, secondsSeeding: secondsSeeding);

    [Test]
    public async Task Cleanup_BothGoalsDisabled_DoesNothing()
    {
        (SeedingLimitCleanup cleanup, FakeTorrentDownloader torrents, _, _) =
            Create(TestConfigs.Seeding(maxRatio: 0, maxTimeMinutes: 0));

        await cleanup.CleanupCompletedSeeding();

        await Assert.That(torrents.FiltersRequested).IsEmpty();
        await Assert.That(torrents.Deleted).IsEmpty();
    }

    [Test]
    public async Task Cleanup_RatioReached_RemovesWithFiles()
    {
        (SeedingLimitCleanup cleanup, FakeTorrentDownloader torrents, List<TorrentDeletedEvent> events, _) =
            Create(TestConfigs.Seeding(maxRatio: 2.0, minTimeMinutes: 30));
        torrents.TorrentsByFilter["all"] = [Finished("h1", ratio: 2.5, secondsSeeding: 3600)];

        await cleanup.CleanupCompletedSeeding();

        await Assert.That(torrents.Deleted.Count).IsEqualTo(1);
        await Assert.That(torrents.Deleted[0].Id).IsEqualTo("h1");
        await Assert.That(torrents.Deleted[0].Cleanup).IsEqualTo(TorrentFileCleanup.DeleteFiles);
        await Assert.That(events.Count).IsEqualTo(1);
        await Assert.That(events[0].Reason).IsEqualTo(TorrentDeleteReason.SeedingCompleted);
    }

    [Test]
    public async Task Cleanup_TimeReached_Removes()
    {
        (SeedingLimitCleanup cleanup, FakeTorrentDownloader torrents, _, _) =
            Create(TestConfigs.Seeding(maxRatio: 2.0, maxTimeMinutes: 1440, minTimeMinutes: 30));
        // Never uploaded, but seeded for the full 24h window.
        torrents.TorrentsByFilter["all"] = [Finished("h1", ratio: 0, secondsSeeding: 1440 * 60)];

        await cleanup.CleanupCompletedSeeding();

        await Assert.That(torrents.Deleted.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Cleanup_BelowMinSeedTime_IsKept()
    {
        (SeedingLimitCleanup cleanup, FakeTorrentDownloader torrents, _, _) =
            Create(TestConfigs.Seeding(maxRatio: 2.0, minTimeMinutes: 30));
        torrents.TorrentsByFilter["all"] = [Finished("h1", ratio: 99, secondsSeeding: 60)];

        await cleanup.CleanupCompletedSeeding();

        await Assert.That(torrents.Deleted).IsEmpty();
    }

    [Test]
    public async Task Cleanup_StillDownloading_IsKept()
    {
        (SeedingLimitCleanup cleanup, FakeTorrentDownloader torrents, _, _) =
            Create(TestConfigs.Seeding());
        torrents.TorrentsByFilter["all"] =
            [TestConfigs.Torrent("h1", state: TorrentState.Downloading, progress: 0.5, ratio: 99, secondsSeeding: 86400)];

        await cleanup.CleanupCompletedSeeding();

        await Assert.That(torrents.Deleted).IsEmpty();
    }

    [Test]
    public async Task Cleanup_BelowBothGoals_IsKept()
    {
        (SeedingLimitCleanup cleanup, FakeTorrentDownloader torrents, _, _) =
            Create(TestConfigs.Seeding(maxRatio: 2.0, maxTimeMinutes: 1440, minTimeMinutes: 30));
        torrents.TorrentsByFilter["all"] = [Finished("h1", ratio: 1.0, secondsSeeding: 3600)];

        await cleanup.CleanupCompletedSeeding();

        await Assert.That(torrents.Deleted).IsEmpty();
    }

    [Test]
    public async Task Cleanup_RecordsSeedingCompletedNotification()
    {
        (SeedingLimitCleanup cleanup, FakeTorrentDownloader torrents, _, FakeEventStore store) =
            Create(TestConfigs.Seeding());
        torrents.TorrentsByFilter["all"] = [Finished("h1", ratio: 3.0, secondsSeeding: 3600)];

        await cleanup.CleanupCompletedSeeding();

        await Assert.That(store.Records.Count).IsEqualTo(1);
        await Assert.That(store.Notifications.Count).IsEqualTo(1);
        await Assert.That(store.Notifications[0].Key).IsEqualTo(DownloadNotificationTopics.SeedingCompleted);
    }
}
