using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Stackarr.Core;
using Stackarr.Core.Models;
using Stackarr.Core.Services;
using Stackarr.DownloadManagement.Jobs;
using Stackarr.TestSupport;

namespace Stackarr.DownloadManagement.Tests;

public class StorageCheckJobTests
{
    static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    static (StorageCheckJob Job, FakeTorrentDownloader Torrents, FakeDiskSpaceProvider Disk, TestTimeProvider Time)
        Create(TorrentDownloaderConfig config)
    {
        FakeTorrentDownloader torrents = new();
        ServiceCollection services = new();
        services.AddSingleton<ITorrentDownloader>(torrents);
        ServiceProvider provider = services.BuildServiceProvider();

        FakeDiskSpaceProvider disk = new();
        TestTimeProvider time = new(Start);
        StorageCheckJob job = new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new MutableOptionsMonitor<TorrentDownloaderConfig>(config),
            disk,
            time,
            new FakeEventStore(),
            NullLogger<StorageCheckJob>.Instance);
        return (job, torrents, disk, time);
    }

    static TorrentItem Seeding(string hash, DateTime addedOn, double ratio = 1.0)
        => TestConfigs.Torrent(hash, state: TorrentState.Seeding, progress: 1.0, ratio: ratio, addedOn: addedOn);

    [Test]
    public async Task Execute_WhenStorageDisabled_DoesNotProbeDisk()
    {
        (StorageCheckJob job, FakeTorrentDownloader torrents, FakeDiskSpaceProvider disk, _) = Create(TestConfigs.Storage(enabled: false));

        await job.Execute(CancellationToken.None);

        await Assert.That(disk.Calls).IsEqualTo(0);
        await Assert.That(torrents.FiltersRequested).IsEmpty();
    }

    [Test]
    public async Task Execute_WhenDiskProbeFails_DoesNothing()
    {
        (StorageCheckJob job, FakeTorrentDownloader torrents, FakeDiskSpaceProvider disk, _) = Create(TestConfigs.Storage());
        disk.Result = false;

        await job.Execute(CancellationToken.None);

        await Assert.That(disk.Calls).IsEqualTo(1);
        await Assert.That(torrents.PauseAllCount).IsEqualTo(0);
        await Assert.That(torrents.Deleted).IsEmpty();
    }

    [Test]
    public async Task Execute_WhenTotalIsZero_DoesNothing()
    {
        (StorageCheckJob job, FakeTorrentDownloader torrents, FakeDiskSpaceProvider disk, _) = Create(TestConfigs.Storage());
        disk.Total = 0;
        disk.Used = 0;

        await job.Execute(CancellationToken.None);

        await Assert.That(torrents.PauseAllCount).IsEqualTo(0);
        await Assert.That(torrents.Deleted).IsEmpty();
    }

    [Test]
    public async Task Execute_BelowCleanupThreshold_DoesNothing()
    {
        (StorageCheckJob job, FakeTorrentDownloader torrents, FakeDiskSpaceProvider disk, _) = Create(TestConfigs.Storage(cleanupSeedingAtPercent: 85));
        disk.Total = 1000;
        disk.Used = 500;

        await job.Execute(CancellationToken.None);

        await Assert.That(torrents.PauseAllCount).IsEqualTo(0);
        await Assert.That(torrents.Deleted).IsEmpty();
        await Assert.That(torrents.FiltersRequested).IsEmpty();
    }

    [Test]
    public async Task Execute_AtCleanupThreshold_CleansSeedingTorrents()
    {
        (StorageCheckJob job, FakeTorrentDownloader torrents, FakeDiskSpaceProvider disk, TestTimeProvider time) =
            Create(TestConfigs.Storage(cleanupSeedingAtPercent: 85, pauseDownloadsAtPercent: 90, minSeedTimeMinutes: 30));
        disk.Total = 1000;
        disk.Used = 850;
        torrents.TorrentsByFilter["seeding"] = [Seeding("old", time.Now.UtcDateTime.AddHours(-1))];

        await job.Execute(CancellationToken.None);

        await Assert.That(torrents.FiltersRequested).Contains("seeding");
        await Assert.That(torrents.Deleted.Count).IsEqualTo(1);
        await Assert.That(torrents.Deleted[0].Id).IsEqualTo("old");
        await Assert.That(torrents.Deleted[0].Cleanup).IsEqualTo(TorrentFileCleanup.DeleteFiles);
        await Assert.That(torrents.PauseAllCount).IsEqualTo(0);
    }

    [Test]
    public async Task Execute_AtPauseThreshold_PausesAllAndDoesNotClean()
    {
        (StorageCheckJob job, FakeTorrentDownloader torrents, FakeDiskSpaceProvider disk, _) = Create(TestConfigs.Storage(pauseDownloadsAtPercent: 90));
        disk.Total = 1000;
        disk.Used = 900;
        torrents.TorrentsByFilter["seeding"] = [Seeding("old", Start.UtcDateTime.AddHours(-1))];

        await job.Execute(CancellationToken.None);

        await Assert.That(torrents.PauseAllCount).IsEqualTo(1);
        await Assert.That(torrents.Deleted).IsEmpty();
    }

    [Test]
    public async Task Execute_BetweenCleanupAndPause_CleansButDoesNotPause()
    {
        (StorageCheckJob job, FakeTorrentDownloader torrents, FakeDiskSpaceProvider disk, TestTimeProvider time) =
            Create(TestConfigs.Storage(cleanupSeedingAtPercent: 85, pauseDownloadsAtPercent: 90));
        disk.Total = 1000;
        disk.Used = 870;
        torrents.TorrentsByFilter["seeding"] = [Seeding("old", time.Now.UtcDateTime.AddHours(-1))];

        await job.Execute(CancellationToken.None);

        await Assert.That(torrents.Deleted.Count).IsEqualTo(1);
        await Assert.That(torrents.PauseAllCount).IsEqualTo(0);
    }

    [Test]
    public async Task Execute_Cleanup_RespectsMinSeedTime()
    {
        (StorageCheckJob job, FakeTorrentDownloader torrents, FakeDiskSpaceProvider disk, TestTimeProvider time) =
            Create(TestConfigs.Storage(minSeedTimeMinutes: 30));
        disk.Total = 1000;
        disk.Used = 860;
        torrents.TorrentsByFilter["seeding"] =
        [
            Seeding("too-new", time.Now.UtcDateTime.AddMinutes(-10)),
            Seeding("eligible", time.Now.UtcDateTime.AddMinutes(-40))
        ];

        await job.Execute(CancellationToken.None);

        await Assert.That(torrents.Deleted.Count).IsEqualTo(1);
        await Assert.That(torrents.Deleted[0].Id).IsEqualTo("eligible");
    }

    [Test]
    public async Task Execute_Cleanup_NoSeedingTorrents_DoesNotDelete()
    {
        (StorageCheckJob job, FakeTorrentDownloader torrents, FakeDiskSpaceProvider disk, _) = Create(TestConfigs.Storage());
        disk.Total = 1000;
        disk.Used = 860;
        torrents.TorrentsByFilter["seeding"] = [];

        await job.Execute(CancellationToken.None);

        await Assert.That(torrents.Deleted).IsEmpty();
    }

    [Test]
    public async Task Execute_Cleanup_OldestStrategy_DeletesOldestFirst()
    {
        (StorageCheckJob job, FakeTorrentDownloader torrents, FakeDiskSpaceProvider disk, TestTimeProvider time) =
            Create(TestConfigs.Storage(cleanupStrategy: "oldest", minSeedTimeMinutes: 0));
        disk.Total = 1000;
        disk.Used = 860; // stays above threshold so all are processed
        torrents.TorrentsByFilter["seeding"] =
        [
            Seeding("middle", time.Now.UtcDateTime.AddHours(-2)),
            Seeding("newest", time.Now.UtcDateTime.AddHours(-1)),
            Seeding("oldest", time.Now.UtcDateTime.AddHours(-3))
        ];

        await job.Execute(CancellationToken.None);

        await Assert.That(torrents.Deleted.Select(d => d.Id)).IsEquivalentTo(["oldest", "middle", "newest"]);
    }

    [Test]
    public async Task Execute_Cleanup_NewestStrategy_DeletesNewestFirst()
    {
        (StorageCheckJob job, FakeTorrentDownloader torrents, FakeDiskSpaceProvider disk, TestTimeProvider time) =
            Create(TestConfigs.Storage(cleanupStrategy: "newest", minSeedTimeMinutes: 0));
        disk.Total = 1000;
        disk.Used = 860;
        torrents.TorrentsByFilter["seeding"] =
        [
            Seeding("middle", time.Now.UtcDateTime.AddHours(-2)),
            Seeding("newest", time.Now.UtcDateTime.AddHours(-1)),
            Seeding("oldest", time.Now.UtcDateTime.AddHours(-3))
        ];

        await job.Execute(CancellationToken.None);

        await Assert.That(torrents.Deleted.Select(d => d.Id)).IsEquivalentTo(["newest", "middle", "oldest"]);
    }

    [Test]
    public async Task Execute_Cleanup_LeastRatioStrategy_DeletesLowestRatioFirst()
    {
        (StorageCheckJob job, FakeTorrentDownloader torrents, FakeDiskSpaceProvider disk, TestTimeProvider time) =
            Create(TestConfigs.Storage(cleanupStrategy: "least_ratio", minSeedTimeMinutes: 0));
        disk.Total = 1000;
        disk.Used = 860;
        torrents.TorrentsByFilter["seeding"] =
        [
            Seeding("high", time.Now.UtcDateTime.AddHours(-2), ratio: 5.0),
            Seeding("low", time.Now.UtcDateTime.AddHours(-2), ratio: 0.1),
            Seeding("mid", time.Now.UtcDateTime.AddHours(-2), ratio: 2.0)
        ];

        await job.Execute(CancellationToken.None);

        await Assert.That(torrents.Deleted.Select(d => d.Id)).IsEquivalentTo(["low", "mid", "high"]);
    }

    [Test]
    public async Task Execute_Cleanup_StopsWhenUsageDropsBelowThreshold()
    {
        (StorageCheckJob job, FakeTorrentDownloader torrents, FakeDiskSpaceProvider disk, TestTimeProvider time) =
            Create(TestConfigs.Storage(cleanupSeedingAtPercent: 85, minSeedTimeMinutes: 0));
        // First probe: 86% (triggers). After first delete: 80% (below threshold -> stop).
        disk.Sequence.Enqueue((true, 1000, 860));
        disk.Sequence.Enqueue((true, 1000, 800));
        torrents.TorrentsByFilter["seeding"] =
        [
            Seeding("a", time.Now.UtcDateTime.AddHours(-1)),
            Seeding("b", time.Now.UtcDateTime.AddHours(-2))
        ];

        await job.Execute(CancellationToken.None);

        await Assert.That(torrents.Deleted.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Execute_Cleanup_StopsWhenPostDeleteProbeFails()
    {
        (StorageCheckJob job, FakeTorrentDownloader torrents, FakeDiskSpaceProvider disk, TestTimeProvider time) =
            Create(TestConfigs.Storage(minSeedTimeMinutes: 0));
        disk.Sequence.Enqueue((true, 1000, 860));
        disk.Sequence.Enqueue((false, 0, 0));
        torrents.TorrentsByFilter["seeding"] =
        [
            Seeding("a", time.Now.UtcDateTime.AddHours(-1)),
            Seeding("b", time.Now.UtcDateTime.AddHours(-2))
        ];

        await job.Execute(CancellationToken.None);

        await Assert.That(torrents.Deleted.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Execute_CancellationDuringQuery_Propagates()
    {
        (StorageCheckJob job, FakeTorrentDownloader torrents, FakeDiskSpaceProvider disk, _) = Create(TestConfigs.Storage());
        disk.Total = 1000;
        disk.Used = 860;
        torrents.GetTorrentsException = new OperationCanceledException();

        await Assert.That(async () => await job.Execute(CancellationToken.None))
            .Throws<OperationCanceledException>();
    }
}
