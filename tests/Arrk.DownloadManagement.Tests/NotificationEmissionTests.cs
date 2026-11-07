using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Arrk.Core;
using Arrk.Core.Events;
using Arrk.Core.Models;
using Arrk.Notifications;
using Arrk.Core.Services;
using Arrk.DownloadManagement.Jobs;

using Arrk.TestSupport;

namespace Arrk.DownloadManagement.Tests;

/// <summary>
/// Verifies that download-management producers attach the correct notification to the events they
/// record, so the notification pipeline has something to deliver.
/// </summary>
public class NotificationEmissionTests
{
    static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    static TorrentItem T(
        string hash,
        TorrentState state = TorrentState.Downloading,
        double progress = 0.0,
        double downloadSpeed = 100,
        int peers = 5,
        string? name = null) => new()
    {
        Hash = hash,
        Name = name ?? hash,
        State = state,
        Progress = progress,
        DownloadSpeed = downloadSpeed,
        Peers = peers
    };

    static (DownloadEventMonitor Monitor, FakeTorrentDownloader Torrents, FakeEventStore Store) Monitor(
        IMediaDownloadEntityResolver? resolver = null)
    {
        FakeTorrentDownloader torrents = new();
        FakeEventStore store = new();
        DownloadEventMonitor monitor = new(
            torrents,
            store,
            resolver is null ? [] : [resolver],
            new TestTimeProvider(Start),
            NullLogger<DownloadEventMonitor>.Instance);
        return (monitor, torrents, store);
    }

    static NotificationMessage? Last(FakeEventStore store) => store.Notifications.LastOrDefault();

    [Test]
    public async Task NewTorrent_AttachesTorrentAddedNotification()
    {
        (DownloadEventMonitor monitor, FakeTorrentDownloader torrents, FakeEventStore store) = Monitor();
        torrents.TorrentsByFilter["all"] = [T("h1", name: "Ubuntu")];

        await monitor.PollAsync();

        NotificationMessage? notification = Last(store);
        await Assert.That(notification).IsNotNull();
        await Assert.That(notification!.Key).IsEqualTo(DownloadNotificationTopics.TorrentAdded);
        await Assert.That(notification.SourceName).IsEqualTo("Ubuntu");
        await Assert.That(notification.Fields.Any(f => f.Name == "Size")).IsTrue();
    }

    [Test]
    public async Task Completion_AttachesDownloadCompletedNotification()
    {
        (DownloadEventMonitor monitor, FakeTorrentDownloader torrents, FakeEventStore store) = Monitor();
        torrents.TorrentsByFilter["all"] = [T("h1", progress: 0.9)];
        await monitor.PollAsync();

        torrents.TorrentsByFilter["all"] = [T("h1", state: TorrentState.Seeding, progress: 1.0, downloadSpeed: 0)];
        await monitor.PollAsync();

        NotificationMessage notification = store.Notifications.Single(n => n.Key == DownloadNotificationTopics.DownloadCompleted);
        await Assert.That(notification.Severity).IsEqualTo(EventSeverity.Success);
    }

    [Test]
    public async Task Error_AttachesTorrentErrorNotification()
    {
        (DownloadEventMonitor monitor, FakeTorrentDownloader torrents, FakeEventStore store) = Monitor();
        torrents.TorrentsByFilter["all"] = [T("h1", progress: 0.5)];
        await monitor.PollAsync();

        torrents.TorrentsByFilter["all"] = [T("h1", state: TorrentState.Error, progress: 0.5, downloadSpeed: 0)];
        await monitor.PollAsync();

        NotificationMessage notification = store.Notifications.Single(n => n.Key == DownloadNotificationTopics.TorrentError);
        await Assert.That(notification.Severity).IsEqualTo(EventSeverity.Error);
    }

    [Test]
    public async Task Stalled_AttachesDownloadStalledNotification()
    {
        (DownloadEventMonitor monitor, FakeTorrentDownloader torrents, FakeEventStore store) = Monitor();
        torrents.TorrentsByFilter["all"] = [T("h1", progress: 0.5, downloadSpeed: 100, peers: 3)];
        await monitor.PollAsync();

        torrents.TorrentsByFilter["all"] = [T("h1", progress: 0.5, downloadSpeed: 0, peers: 0)];
        await monitor.PollAsync();

        await Assert.That(store.Notifications.Any(n => n.Key == DownloadNotificationTopics.DownloadStalled)).IsTrue();
    }

    [Test]
    public async Task Progress_AttachesDownloadProgressNotification()
    {
        (DownloadEventMonitor monitor, FakeTorrentDownloader torrents, FakeEventStore store) = Monitor();
        torrents.TorrentsByFilter["all"] = [T("h1", progress: 0.05)];
        await monitor.PollAsync();

        torrents.TorrentsByFilter["all"] = [T("h1", progress: 0.25)];
        await monitor.PollAsync();

        await Assert.That(store.Notifications.Any(n => n.Key == DownloadNotificationTopics.DownloadProgress)).IsTrue();
    }

    [Test]
    public async Task ResolvedTorrent_TargetsGlobalChannelsAndSubscribers()
    {
        MediaEntityRef entity = new(
            "tv", new MediaDomain("movie"), "tv:movie:tmdb:603", "The Matrix");
        (DownloadEventMonitor monitor, FakeTorrentDownloader torrents, FakeEventStore store) =
            Monitor(new FixedEntityResolver(entity));
        torrents.TorrentsByFilter["all"] = [T("h1", name: "The Matrix")];

        await monitor.PollAsync();

        NotificationMessage notification = store.Notifications.Single();
        await Assert.That(notification.Audience.Global).IsTrue();
        await Assert.That(notification.Audience.FollowersOf).IsNotNull();
        await Assert.That(notification.Audience.FollowersOf!.ItemKey).IsEqualTo("tv:movie:tmdb:603");
        await Assert.That(notification.Title).IsEqualTo("Download started");
        await Assert.That(notification.Body).Contains("The Matrix");
        await Assert.That(notification.SourceName).IsEqualTo("The Matrix");

        torrents.TorrentsByFilter["all"] = [T("h1", state: TorrentState.Seeding, progress: 1.0, downloadSpeed: 0)];
        await monitor.PollAsync();

        NotificationMessage completed = store.Notifications.Single(n => n.Key == DownloadNotificationTopics.DownloadCompleted);
        await Assert.That(completed.Audience.Global).IsTrue();
        await Assert.That(completed.Audience.FollowersOf!.ItemKey).IsEqualTo("tv:movie:tmdb:603");
    }

    [Test]
    public async Task UnresolvedTorrent_StaysABroadcast()
    {
        (DownloadEventMonitor monitor, FakeTorrentDownloader torrents, FakeEventStore store) = Monitor();
        torrents.TorrentsByFilter["all"] = [T("h1", name: "Manual download")];

        await monitor.PollAsync();

        NotificationMessage notification = store.Notifications.Single();
        await Assert.That(notification.Audience.Global).IsTrue();
        await Assert.That(notification.Audience.FollowersOf).IsNull();
    }

    sealed class FixedEntityResolver(MediaEntityRef entity) : IMediaDownloadEntityResolver
    {
        public Task<MediaEntityRef?> ResolveAsync(string torrentHash, CancellationToken ct = default) =>
            Task.FromResult<MediaEntityRef?>(entity);
    }

    [Test]
    public async Task Removed_AttachesTorrentRemovedNotification()
    {
        (DownloadEventMonitor monitor, FakeTorrentDownloader torrents, FakeEventStore store) = Monitor();
        torrents.TorrentsByFilter["all"] = [T("h1", name: "Gone")];
        await monitor.PollAsync();

        torrents.TorrentsByFilter["all"] = [];
        await monitor.PollAsync();

        NotificationMessage notification = store.Notifications.Single(n => n.Key == DownloadNotificationTopics.TorrentRemoved);
        await Assert.That(notification.SourceName).IsEqualTo("Gone");
    }

    [Test]
    public async Task Notifications_DoNotRepeatTheTitleInTheBody()
    {
        (DownloadEventMonitor monitor, FakeTorrentDownloader torrents, FakeEventStore store) = Monitor();
        torrents.TorrentsByFilter["all"] = [T("h1", name: "Demon Slayer")];
        await monitor.PollAsync();

        torrents.TorrentsByFilter["all"] = [];
        await monitor.PollAsync();

        NotificationMessage removed = store.Notifications.Single(n => n.Key == DownloadNotificationTopics.TorrentRemoved);
        await Assert.That(removed.Title).IsEqualTo("Torrent removed");
        await Assert.That(removed.Body).IsEqualTo("Removed torrent Demon Slayer");
        await Assert.That(removed.Body.StartsWith(removed.Title, StringComparison.OrdinalIgnoreCase)).IsFalse();
    }

    // Torrent health detector

    static (TorrentHealthDetector Detector, FakeTorrentDownloader Torrents, FakeEventStore Store, TestTimeProvider Time)
        Detector(TorrentDownloaderConfig config)
    {
        FakeTorrentDownloader torrents = new();
        FakeEventStore store = new();
        TestTimeProvider time = new(Start);
        TorrentHealthDetector detector = new(
            torrents,
            new MutableOptionsMonitor<TorrentDownloaderConfig>(config),
            new FakeConnectionHealthProvider(),
            new EventBus(),
            store,
            new TorrentPenaltyTracker(store, time, NullLogger<TorrentPenaltyTracker>.Instance),
            time,
            NullLogger<TorrentHealthDetector>.Instance);
        return (detector, torrents, store, time);
    }

    [Test]
    public async Task FirstSlowSighting_AttachesSlowWarningNotification()
    {
        (TorrentHealthDetector detector, FakeTorrentDownloader torrents, FakeEventStore store, _) =
            Detector(TestConfigs.Slow());

        torrents.TorrentsByFilter["all"] = [TestConfigs.Torrent("h1")];
        await detector.CheckTorrents();

        NotificationMessage notification = store.Notifications.Single();
        await Assert.That(notification.Key).IsEqualTo(DownloadNotificationTopics.TorrentSlow);
        await Assert.That(notification.Severity).IsEqualTo(EventSeverity.Warning);
    }

    [Test]
    public async Task RemovedTorrent_AttachesTorrentStrikedNotification()
    {
        (TorrentHealthDetector detector, FakeTorrentDownloader torrents, FakeEventStore store, TestTimeProvider time) =
            Detector(TestConfigs.Slow(limitMinutes: 30));

        torrents.TorrentsByFilter["all"] = [TestConfigs.Torrent("h1")];
        await detector.CheckTorrents();
        time.AdvanceMinutes(31);
        await detector.CheckTorrents();

        await Assert.That(store.Notifications.Any(n => n.Key == DownloadNotificationTopics.TorrentStriked)).IsTrue();
    }

    // Error cleanup

    static (ErrorTorrentCleanup Cleanup, FakeTorrentDownloader Torrents, FakeEventStore Store, TestTimeProvider Time)
        Cleanup(TorrentDownloaderConfig config)
    {
        FakeTorrentDownloader torrents = new();
        FakeEventStore store = new();
        TestTimeProvider time = new(Start);
        ErrorTorrentCleanup cleanup = new(
            torrents,
            new MutableOptionsMonitor<TorrentDownloaderConfig>(config),
            new EventBus(),
            store,
            time,
            NullLogger<ErrorTorrentCleanup>.Instance);
        return (cleanup, torrents, store, time);
    }

    [Test]
    public async Task ErroredTorrentCleanup_AttachesTorrentErrorNotification()
    {
        (ErrorTorrentCleanup cleanup, FakeTorrentDownloader torrents, FakeEventStore store, TestTimeProvider time) =
            Cleanup(TestConfigs.Errors(deleteAfterMinutes: 30));

        torrents.TorrentsByFilter["error"] = [TestConfigs.Torrent("h1", state: TorrentState.Error)];
        await cleanup.CleanupErroredTorrents();
        time.AdvanceMinutes(31);
        await cleanup.CleanupErroredTorrents();

        NotificationMessage notification = store.Notifications.Single();
        await Assert.That(notification.Key).IsEqualTo(DownloadNotificationTopics.TorrentError);
        await Assert.That(notification.Severity).IsEqualTo(EventSeverity.Error);
    }

    // Storage check

    static (StorageCheckJob Job, FakeTorrentDownloader Torrents, FakeDiskSpaceProvider Disk, FakeEventStore Store, TestTimeProvider Time)
        Storage(TorrentDownloaderConfig config)
    {
        FakeTorrentDownloader torrents = new();
        FakeDiskSpaceProvider disk = new();
        FakeEventStore store = new();
        TestTimeProvider time = new(Start);

        ServiceCollection services = new();
        services.AddSingleton<ITorrentDownloader>(torrents);
        ServiceProvider provider = services.BuildServiceProvider();

        StorageCheckJob job = new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new MutableOptionsMonitor<TorrentDownloaderConfig>(config),
            disk,
            time,
            store,
            NullLogger<StorageCheckJob>.Instance);
        return (job, torrents, disk, store, time);
    }

    [Test]
    public async Task StoragePause_AttachesCriticalAndPausedNotifications()
    {
        (StorageCheckJob job, _, FakeDiskSpaceProvider disk, FakeEventStore store, _) =
            Storage(TestConfigs.Storage(pauseDownloadsAtPercent: 90));
        disk.Total = 1000;
        disk.Used = 950;

        await job.Execute(CancellationToken.None);

        await Assert.That(store.Notifications.Any(n => n.Key == StorageNotificationTopics.StorageCritical)).IsTrue();
        await Assert.That(store.Notifications.Any(n => n.Key == DownloadNotificationTopics.DownloadPausedStorage)).IsTrue();
    }

    [Test]
    public async Task StorageCleanup_AttachesWarningAndCleanupNotifications()
    {
        (StorageCheckJob job, FakeTorrentDownloader torrents, FakeDiskSpaceProvider disk, FakeEventStore store, TestTimeProvider time) =
            Storage(TestConfigs.Storage(cleanupSeedingAtPercent: 85, pauseDownloadsAtPercent: 90, minSeedTimeMinutes: 0));
        disk.Total = 1000;
        disk.Used = 860;
        torrents.TorrentsByFilter["seeding"] = [TestConfigs.Torrent("old", state: TorrentState.Seeding, progress: 1.0, addedOn: time.Now.UtcDateTime.AddHours(-1))];

        await job.Execute(CancellationToken.None);

        await Assert.That(store.Notifications.Any(n => n.Key == StorageNotificationTopics.StorageLow)).IsTrue();
        await Assert.That(store.Notifications.Any(n => n.Key == StorageNotificationTopics.StorageCleanup)).IsTrue();
    }

    [Test]
    public async Task StorageCleanup_WhenNothingDeleted_EmitsNoNotifications()
    {
        (StorageCheckJob job, FakeTorrentDownloader torrents, FakeDiskSpaceProvider disk, FakeEventStore store, _) =
            Storage(TestConfigs.Storage(cleanupSeedingAtPercent: 85, pauseDownloadsAtPercent: 90));
        disk.Total = 1000;
        disk.Used = 860;
        torrents.TorrentsByFilter["seeding"] = [];

        await job.Execute(CancellationToken.None);

        await Assert.That(store.Notifications).IsEmpty();
    }
}
