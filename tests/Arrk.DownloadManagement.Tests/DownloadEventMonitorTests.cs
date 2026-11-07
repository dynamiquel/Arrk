using Microsoft.Extensions.Logging.Abstractions;
using Arrk.Core.Events;
using Arrk.Core.Models;
using Arrk.Notifications;

using Arrk.TestSupport;
namespace Arrk.DownloadManagement.Tests;

public class DownloadEventMonitorTests
{
    static (DownloadEventMonitor Monitor, FakeTorrentDownloader Torrents, FakeEventStore Store, TestTimeProvider Time) Create()
    {
        FakeTorrentDownloader torrents = new();
        FakeEventStore store = new();
        TestTimeProvider time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        DownloadEventMonitor monitor = new(torrents, store, [], time, NullLogger<DownloadEventMonitor>.Instance);
        return (monitor, torrents, store, time);
    }

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

    [Test]
    public async Task Poll_NewTorrent_RecordsAddedEvent()
    {
        (DownloadEventMonitor monitor, FakeTorrentDownloader torrents, FakeEventStore store, _) = Create();
        torrents.TorrentsByFilter["all"] = [T("h1", name: "Ubuntu")];

        await monitor.PollAsync();

        await Assert.That(store.Events.Count).IsEqualTo(1);
        await Assert.That(store.Events[0].EventType).IsEqualTo(DownloadEventType.Added);
        await Assert.That(store.Events[0].Category).IsEqualTo(EventCategory.Download);
        await Assert.That(store.Events[0].SourceName).IsEqualTo("Ubuntu");
    }

    [Test]
    public async Task Poll_SameTorrentTwice_DoesNotDuplicateAdded()
    {
        (DownloadEventMonitor monitor, FakeTorrentDownloader torrents, FakeEventStore store, _) = Create();
        torrents.TorrentsByFilter["all"] = [T("h1")];

        await monitor.PollAsync();
        await monitor.PollAsync();

        await Assert.That(store.Events.Count(e => e.EventType == DownloadEventType.Added)).IsEqualTo(1);
    }

    [Test]
    public async Task Poll_NewMonitorWithExistingEvents_DoesNotReannounceAdded()
    {
        // Simulate a restart: a fresh monitor whose event store already knows the torrent.
        FakeTorrentDownloader torrents = new();
        FakeEventStore store = new();
        torrents.TorrentsByFilter["all"] = [T("h1", progress: 0.5)];

        DownloadEventMonitor first = new(torrents, store, [], new TestTimeProvider(DateTimeOffset.UtcNow), NullLogger<DownloadEventMonitor>.Instance);
        await first.PollAsync();
        await Assert.That(store.Events.Count(e => e.EventType == DownloadEventType.Added)).IsEqualTo(1);

        // New process/monitor instance, same store.
        DownloadEventMonitor restarted = new(torrents, store, [], new TestTimeProvider(DateTimeOffset.UtcNow), NullLogger<DownloadEventMonitor>.Instance);
        await restarted.PollAsync();

        await Assert.That(store.Events.Count(e => e.EventType == DownloadEventType.Added)).IsEqualTo(1);
    }

    [Test]
    public async Task Poll_CompletedTorrent_RecordsCompletedOnce_NotRepeatedly()
    {
        (DownloadEventMonitor monitor, FakeTorrentDownloader torrents, FakeEventStore store, _) = Create();
        torrents.TorrentsByFilter["all"] = [T("h1", progress: 1.0, state: TorrentState.Seeding, downloadSpeed: 0)];
        await monitor.PollAsync();

        // Poll several more times with the torrent still complete.
        await monitor.PollAsync();
        await monitor.PollAsync();
        await monitor.PollAsync();

        await Assert.That(store.Events.Count(e => e.EventType == DownloadEventType.Completed)).IsEqualTo(0);

        // Cross the boundary explicitly and confirm exactly one completed event.
        (DownloadEventMonitor m2, FakeTorrentDownloader t2, FakeEventStore s2, _) = Create();
        t2.TorrentsByFilter["all"] = [T("h1", progress: 0.9)];
        await m2.PollAsync();
        t2.TorrentsByFilter["all"] = [T("h1", progress: 1.0, state: TorrentState.Seeding)];
        await m2.PollAsync();
        await m2.PollAsync();
        await m2.PollAsync();

        await Assert.That(s2.Events.Count(e => e.EventType == DownloadEventType.Completed)).IsEqualTo(1);
    }

    [Test]
    public async Task Poll_CompletedTorrentOnRestart_DoesNotReannounceCompleted()
    {
        FakeTorrentDownloader torrents = new();
        FakeEventStore store = new();
        TestTimeProvider clock = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        DownloadEventMonitor first = new(torrents, store, [], clock, NullLogger<DownloadEventMonitor>.Instance);
        torrents.TorrentsByFilter["all"] = [T("h1", progress: 0.9)];
        await first.PollAsync();
        torrents.TorrentsByFilter["all"] = [T("h1", progress: 1.0, state: TorrentState.Seeding)];
        await first.PollAsync();
        await Assert.That(store.Events.Count(e => e.EventType == DownloadEventType.Completed)).IsEqualTo(1);

        // Restart with the torrent still complete; hydration must suppress a second completed event.
        DownloadEventMonitor restarted = new(torrents, store, [], clock, NullLogger<DownloadEventMonitor>.Instance);
        await restarted.PollAsync();
        await restarted.PollAsync();

        await Assert.That(store.Events.Count(e => e.EventType == DownloadEventType.Completed)).IsEqualTo(1);
    }

    [Test]
    public async Task Poll_CrossesEachTenPercentBoundary_RecordsProgressSteps()
    {
        (DownloadEventMonitor monitor, FakeTorrentDownloader torrents, FakeEventStore store, _) = Create();

        // 5% -> 15% -> 35% crosses buckets 0,1,3 => progress events at 10%, 30%.
        foreach (double progress in new[] { 0.05, 0.15, 0.35 })
        {
            torrents.TorrentsByFilter["all"] = [T("h1", progress: progress)];
            await monitor.PollAsync();
        }

        List<SystemEvent> progressEvents = store.Events.Where(e => e.EventType == DownloadEventType.Progress).ToList();
        await Assert.That(progressEvents.Count).IsEqualTo(2);
        await Assert.That(progressEvents[0].Message).Contains("10%");
        await Assert.That(progressEvents[1].Message).Contains("30%");
    }

    [Test]
    public async Task Poll_MultipleStepsInOneJump_RecordsSingleHighestStep()
    {
        (DownloadEventMonitor monitor, FakeTorrentDownloader torrents, FakeEventStore store, _) = Create();

        torrents.TorrentsByFilter["all"] = [T("h1", progress: 0.01)];
        await monitor.PollAsync();
        torrents.TorrentsByFilter["all"] = [T("h1", progress: 0.55)];
        await monitor.PollAsync();

        List<SystemEvent> progressEvents = store.Events.Where(e => e.EventType == DownloadEventType.Progress).ToList();
        await Assert.That(progressEvents.Count).IsEqualTo(1);
        await Assert.That(progressEvents[0].Message).Contains("50%");
    }

    [Test]
    public async Task Poll_Completion_RecordsCompletedSuccessEvent()
    {
        (DownloadEventMonitor monitor, FakeTorrentDownloader torrents, FakeEventStore store, _) = Create();
        torrents.TorrentsByFilter["all"] = [T("h1", progress: 0.9)];
        await monitor.PollAsync();

        torrents.TorrentsByFilter["all"] = [T("h1", state: TorrentState.Seeding, progress: 1.0, downloadSpeed: 0)];
        await monitor.PollAsync();

        SystemEvent completed = store.Events.Single(e => e.EventType == DownloadEventType.Completed);
        await Assert.That(completed.Severity).IsEqualTo("Success");
    }

    [Test]
    public async Task Poll_Completion_DoesNotAlsoEmitProgressAt100()
    {
        (DownloadEventMonitor monitor, FakeTorrentDownloader torrents, FakeEventStore store, _) = Create();
        torrents.TorrentsByFilter["all"] = [T("h1", progress: 0.95)];
        await monitor.PollAsync();
        torrents.TorrentsByFilter["all"] = [T("h1", progress: 1.0, state: TorrentState.Seeding)];
        await monitor.PollAsync();

        await Assert.That(store.Events.Any(e => e.EventType == DownloadEventType.Progress)).IsFalse();
    }

    [Test]
    public async Task Poll_Paused_RecordsPausedEvent()
    {
        (DownloadEventMonitor monitor, FakeTorrentDownloader torrents, FakeEventStore store, _) = Create();
        torrents.TorrentsByFilter["all"] = [T("h1", progress: 0.5)];
        await monitor.PollAsync();

        torrents.TorrentsByFilter["all"] = [T("h1", state: TorrentState.Paused, progress: 0.5, downloadSpeed: 0)];
        await monitor.PollAsync();

        await Assert.That(store.Events.Any(e => e.EventType == DownloadEventType.Paused)).IsTrue();
    }

    [Test]
    public async Task Poll_ResumedFromPaused_RecordsResumedEvent()
    {
        (DownloadEventMonitor monitor, FakeTorrentDownloader torrents, FakeEventStore store, _) = Create();
        torrents.TorrentsByFilter["all"] = [T("h1", state: TorrentState.Paused, progress: 0.5, downloadSpeed: 0)];
        await monitor.PollAsync();

        torrents.TorrentsByFilter["all"] = [T("h1", state: TorrentState.Downloading, progress: 0.5, downloadSpeed: 100)];
        await monitor.PollAsync();

        await Assert.That(store.Events.Any(e => e.EventType == DownloadEventType.Resumed)).IsTrue();
    }

    [Test]
    public async Task Poll_ErrorState_RecordsErrorEvent()
    {
        (DownloadEventMonitor monitor, FakeTorrentDownloader torrents, FakeEventStore store, _) = Create();
        torrents.TorrentsByFilter["all"] = [T("h1", progress: 0.5)];
        await monitor.PollAsync();

        torrents.TorrentsByFilter["all"] = [T("h1", state: TorrentState.Error, progress: 0.5, downloadSpeed: 0)];
        await monitor.PollAsync();

        SystemEvent error = store.Events.Single(e => e.EventType == DownloadEventType.Error);
        await Assert.That(error.Severity).IsEqualTo("Error");
    }

    [Test]
    public async Task Poll_Stalled_RecordsStalledOnce()
    {
        (DownloadEventMonitor monitor, FakeTorrentDownloader torrents, FakeEventStore store, _) = Create();
        torrents.TorrentsByFilter["all"] = [T("h1", progress: 0.5, downloadSpeed: 100, peers: 3)];
        await monitor.PollAsync();

        torrents.TorrentsByFilter["all"] = [T("h1", progress: 0.5, downloadSpeed: 0, peers: 0)];
        await monitor.PollAsync();
        await monitor.PollAsync();

        await Assert.That(store.Events.Count(e => e.EventType == DownloadEventType.Stalled)).IsEqualTo(1);
    }

    [Test]
    public async Task Poll_RecoversFromStall_CanStallAgain()
    {
        (DownloadEventMonitor monitor, FakeTorrentDownloader torrents, FakeEventStore store, _) = Create();

        torrents.TorrentsByFilter["all"] = [T("h1", progress: 0.5, downloadSpeed: 0, peers: 0)];
        await monitor.PollAsync();                                  // baseline (already stalled)
        await monitor.PollAsync();                                  // stall #1
        torrents.TorrentsByFilter["all"] = [T("h1", progress: 0.5, downloadSpeed: 100, peers: 3)];
        await monitor.PollAsync();                                  // recovered
        torrents.TorrentsByFilter["all"] = [T("h1", progress: 0.5, downloadSpeed: 0, peers: 0)];
        await monitor.PollAsync();                                  // stall #2

        await Assert.That(store.Events.Count(e => e.EventType == DownloadEventType.Stalled)).IsEqualTo(2);
    }

    [Test]
    public async Task Poll_TorrentDisappears_RecordsRemovedEvent()
    {
        (DownloadEventMonitor monitor, FakeTorrentDownloader torrents, FakeEventStore store, _) = Create();
        torrents.TorrentsByFilter["all"] = [T("h1", name: "Gone")];
        await monitor.PollAsync();

        torrents.TorrentsByFilter["all"] = [];
        await monitor.PollAsync();

        SystemEvent removed = store.Events.Single(e => e.EventType == DownloadEventType.Removed);
        await Assert.That(removed.Severity).IsEqualTo("Warning");
        await Assert.That(removed.SourceName).IsEqualTo("Gone");
    }

    [Test]
    public async Task Poll_RestartAfterRemoval_DoesNotReannounceRemoved()
    {
        FakeTorrentDownloader torrents = new();
        FakeEventStore store = new();
        TestTimeProvider clock = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        DownloadEventMonitor first = new(torrents, store, [], clock, NullLogger<DownloadEventMonitor>.Instance);
        torrents.TorrentsByFilter["all"] = [T("h1", name: "Gone")];
        await first.PollAsync();   // added
        torrents.TorrentsByFilter["all"] = [];
        await first.PollAsync();   // removed
        await Assert.That(store.Events.Count(e => e.EventType == DownloadEventType.Removed)).IsEqualTo(1);

        // Restart with the torrent still gone. Hydration must treat the removal as terminal and
        // not resurrect the older Added event, otherwise every restart spams "torrent removed".
        DownloadEventMonitor restarted = new(torrents, store, [], clock, NullLogger<DownloadEventMonitor>.Instance);
        await restarted.PollAsync();
        await restarted.PollAsync();

        await Assert.That(store.Events.Count(e => e.EventType == DownloadEventType.Removed)).IsEqualTo(1);
        await Assert.That(store.Notifications.Count(n => n.Key == DownloadNotificationTopics.TorrentRemoved)).IsEqualTo(1);
    }

    [Test]
    public async Task Poll_MultipleTorrents_Independent()
    {
        (DownloadEventMonitor monitor, FakeTorrentDownloader torrents, FakeEventStore store, _) = Create();
        torrents.TorrentsByFilter["all"] = [T("a"), T("b")];

        await monitor.PollAsync();

        await Assert.That(store.Events.Count(e => e.EventType == DownloadEventType.Added)).IsEqualTo(2);
    }

    [Test]
    public async Task Poll_QueryFails_DoesNotThrowOrRecord()
    {
        (DownloadEventMonitor monitor, FakeTorrentDownloader torrents, FakeEventStore store, _) = Create();
        torrents.GetTorrentsException = new HttpRequestException("transmission down");

        await monitor.PollAsync();

        await Assert.That(store.Events).IsEmpty();
    }

    [Test]
    public async Task Poll_EventStoreFails_DoesNotThrow()
    {
        FaultyEventStore faulty = new();
        FakeTorrentDownloader torrents = new();
        torrents.TorrentsByFilter["all"] = [T("h1")];
        DownloadEventMonitor monitor = new(torrents, faulty, [], new TestTimeProvider(DateTimeOffset.UtcNow), NullLogger<DownloadEventMonitor>.Instance);

        await monitor.PollAsync();
    }

    [Test]
    public async Task Poll_NoTorrents_NothingRecorded()
    {
        (DownloadEventMonitor monitor, FakeTorrentDownloader torrents, FakeEventStore store, _) = Create();
        torrents.TorrentsByFilter["all"] = [];

        await monitor.PollAsync();

        await Assert.That(store.Events).IsEmpty();
    }

    [Test]
    public async Task Poll_Cancellation_Propagates()
    {
        (DownloadEventMonitor monitor, FakeTorrentDownloader torrents, _, _) = Create();
        using CancellationTokenSource cts = new();
        cts.Cancel();
        torrents.GetTorrentsException = new OperationCanceledException(cts.Token);

        await Assert.That(async () => await monitor.PollAsync(cts.Token))
            .Throws<OperationCanceledException>();
    }

    [Test]
    public async Task Poll_ConcurrentRuns_DoNotThrow()
    {
        (DownloadEventMonitor monitor, FakeTorrentDownloader torrents, _, _) = Create();
        torrents.TorrentsByFilter["all"] = [T("h1"), T("h2")];

        await Assert.That(async () => await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => monitor.PollAsync())))
            .ThrowsNothing();
    }

    sealed class FaultyEventStore : IEventStore
    {
        public Task RecordAsync(SystemEventRecord record, CancellationToken ct = default)
            => throw new InvalidOperationException("db down");

        public Task RecordAsync(IEnumerable<SystemEventRecord> records, CancellationToken ct = default)
            => throw new InvalidOperationException("db down");

        public Task<IReadOnlyList<SystemEvent>> QueryAsync(SystemEventQuery query, CancellationToken ct = default)
            => throw new InvalidOperationException("db down");

        public Task<int> PurgeOlderThanAsync(DateTime cutoff, CancellationToken ct = default)
            => throw new InvalidOperationException("db down");

        public Task<int> PurgeAsync(
            DateTime defaultCutoff,
            IReadOnlyDictionary<string, DateTime?> topicCutoffs,
            CancellationToken ct = default) => throw new InvalidOperationException("db down");

        public Task<IReadOnlyList<string>> GetEventTypesAsync(string? category = null, CancellationToken ct = default)
            => throw new InvalidOperationException("db down");
    }
}
