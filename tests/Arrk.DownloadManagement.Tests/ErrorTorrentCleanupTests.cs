using Microsoft.Extensions.Logging.Abstractions;
using Arrk.Core;
using Arrk.Core.Events;
using Arrk.Core.Models;

using Arrk.TestSupport;
namespace Arrk.DownloadManagement.Tests;

public class ErrorTorrentCleanupTests
{
    static (ErrorTorrentCleanup Cleanup, FakeTorrentDownloader Torrents, TestTimeProvider Time, List<TorrentDeletedEvent> Events, FakeEventStore Store)
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
        ErrorTorrentCleanup cleanup = new(
            torrents,
            new MutableOptionsMonitor<TorrentDownloaderConfig>(config),
            bus,
            store,
            time,
            NullLogger<ErrorTorrentCleanup>.Instance);
        return (cleanup, torrents, time, events, store);
    }

    [Test]
    public async Task Cleanup_WhenDisabled_DoesNotQuery()
    {
        (ErrorTorrentCleanup cleanup, FakeTorrentDownloader torrents, _, _, _) = Create(TestConfigs.Errors(enabled: false));

        await cleanup.CleanupErroredTorrents();

        await Assert.That(torrents.FiltersRequested).IsEmpty();
        await Assert.That(torrents.Deleted).IsEmpty();
    }

    [Test]
    public async Task Cleanup_QueriesErrorFilter()
    {
        (ErrorTorrentCleanup cleanup, FakeTorrentDownloader torrents, _, _, _) = Create(TestConfigs.Errors());

        await cleanup.CleanupErroredTorrents();

        await Assert.That(torrents.FiltersRequested).Contains("error");
    }

    [Test]
    public async Task Cleanup_FirstSighting_DoesNotDelete()
    {
        (ErrorTorrentCleanup cleanup, FakeTorrentDownloader torrents, _, _, _) = Create(TestConfigs.Errors());
        torrents.TorrentsByFilter["error"] = [TestConfigs.Torrent("h1", state: TorrentState.Error)];

        await cleanup.CleanupErroredTorrents();

        await Assert.That(torrents.Deleted).IsEmpty();
    }

    [Test]
    public async Task Cleanup_AfterDeleteWindow_DeletesAndPublishesEvent()
    {
        (ErrorTorrentCleanup cleanup, FakeTorrentDownloader torrents, TestTimeProvider time, List<TorrentDeletedEvent> events, _) =
            Create(TestConfigs.Errors(deleteAfterMinutes: 30));
        torrents.TorrentsByFilter["error"] = [TestConfigs.Torrent("h1", state: TorrentState.Error)];

        await cleanup.CleanupErroredTorrents();
        time.AdvanceMinutes(31);
        await cleanup.CleanupErroredTorrents();

        await Assert.That(torrents.Deleted.Count).IsEqualTo(1);
        await Assert.That(torrents.Deleted[0].Id).IsEqualTo("h1");
        await Assert.That(events.Count).IsEqualTo(1);
        await Assert.That(events[0].Reason).IsEqualTo(TorrentDeleteReason.Error);
    }

    [Test]
    public async Task Cleanup_WithinDeleteWindow_DoesNotDelete()
    {
        (ErrorTorrentCleanup cleanup, FakeTorrentDownloader torrents, TestTimeProvider time, _, _) = Create(TestConfigs.Errors(deleteAfterMinutes: 30));
        torrents.TorrentsByFilter["error"] = [TestConfigs.Torrent("h1", state: TorrentState.Error)];

        await cleanup.CleanupErroredTorrents();
        time.AdvanceMinutes(29);
        await cleanup.CleanupErroredTorrents();

        await Assert.That(torrents.Deleted).IsEmpty();
    }

    [Test]
    public async Task Cleanup_ZeroWindow_DeletesOnSecondObservation()
    {
        (ErrorTorrentCleanup cleanup, FakeTorrentDownloader torrents, _, _, _) = Create(TestConfigs.Errors(deleteAfterMinutes: 0));
        torrents.TorrentsByFilter["error"] = [TestConfigs.Torrent("h1", state: TorrentState.Error)];

        await cleanup.CleanupErroredTorrents();
        await cleanup.CleanupErroredTorrents();

        await Assert.That(torrents.Deleted.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Cleanup_RecoveredTorrent_ResetsTimer()
    {
        (ErrorTorrentCleanup cleanup, FakeTorrentDownloader torrents, TestTimeProvider time, _, _) = Create(TestConfigs.Errors(deleteAfterMinutes: 30));

        torrents.TorrentsByFilter["error"] = [TestConfigs.Torrent("h1", state: TorrentState.Error)];
        await cleanup.CleanupErroredTorrents();      // T0

        time.AdvanceMinutes(10);
        torrents.TorrentsByFilter["error"] = [];
        await cleanup.CleanupErroredTorrents();      // cleared

        time.AdvanceMinutes(10);
        torrents.TorrentsByFilter["error"] = [TestConfigs.Torrent("h1", state: TorrentState.Error)];
        await cleanup.CleanupErroredTorrents();      // T+20

        time.AdvanceMinutes(20);                     // T+40, 20 min since re-error
        await cleanup.CleanupErroredTorrents();
        await Assert.That(torrents.Deleted).IsEmpty();

        time.AdvanceMinutes(11);                     // T+51
        await cleanup.CleanupErroredTorrents();
        await Assert.That(torrents.Deleted.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Cleanup_NoErroredTorrents_DoesNothing()
    {
        (ErrorTorrentCleanup cleanup, FakeTorrentDownloader torrents, TestTimeProvider time, _, _) = Create(TestConfigs.Errors(deleteAfterMinutes: 0));
        torrents.TorrentsByFilter["error"] = [];

        await cleanup.CleanupErroredTorrents();
        time.AdvanceMinutes(60);
        await cleanup.CleanupErroredTorrents();

        await Assert.That(torrents.Deleted).IsEmpty();
    }

    [Test]
    public async Task Cleanup_MultipleErroredTorrents_Independent()
    {
        (ErrorTorrentCleanup cleanup, FakeTorrentDownloader torrents, TestTimeProvider time, _, _) = Create(TestConfigs.Errors(deleteAfterMinutes: 30));
        torrents.TorrentsByFilter["error"] = [TestConfigs.Torrent("a", state: TorrentState.Error), TestConfigs.Torrent("b", state: TorrentState.Error)];

        await cleanup.CleanupErroredTorrents();
        time.AdvanceMinutes(31);
        // "a" is no longer errored -> its timer is cleared without deletion.
        torrents.TorrentsByFilter["error"] = [TestConfigs.Torrent("b", state: TorrentState.Error)];
        await cleanup.CleanupErroredTorrents();

        await Assert.That(torrents.Deleted.Count).IsEqualTo(1);
        await Assert.That(torrents.Deleted[0].Id).IsEqualTo("b");
    }

    [Test]
    public async Task Cleanup_CancellationDuringQuery_Propagates()
    {
        (ErrorTorrentCleanup cleanup, FakeTorrentDownloader torrents, _, _, _) = Create(TestConfigs.Errors());
        torrents.GetTorrentsException = new OperationCanceledException();

        await Assert.That(async () => await cleanup.CleanupErroredTorrents())
            .Throws<OperationCanceledException>();
    }

    [Test]
    public async Task Cleanup_ConcurrentRuns_DeleteExactlyOnce()
    {
        (ErrorTorrentCleanup cleanup, FakeTorrentDownloader torrents, TestTimeProvider time, List<TorrentDeletedEvent> events, _) =
            Create(TestConfigs.Errors(deleteAfterMinutes: 30));
        torrents.TorrentsByFilter["error"] = [TestConfigs.Torrent("h1", state: TorrentState.Error)];

        await cleanup.CleanupErroredTorrents();
        time.AdvanceMinutes(31);

        IEnumerable<Task> runs = Enumerable.Range(0, 25).Select(_ => cleanup.CleanupErroredTorrents());
        await Task.WhenAll(runs);

        await Assert.That(torrents.Deleted.Count).IsEqualTo(1);
        await Assert.That(events.Count).IsEqualTo(1);
    }
}
