using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stackarr.Core;
using Stackarr.Core.Events;
using Stackarr.Core.Models;
using Stackarr.Core.Services;
using Stackarr.DownloadManagement.Jobs;

using Stackarr.TestSupport;
namespace Stackarr.DownloadManagement.Tests;

public class JobsTests
{
    static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    static ServiceProvider BuildProvider(
        FakeTorrentDownloader torrents,
        TorrentDownloaderConfig config,
        FakeDiskSpaceProvider? disk = null)
    {
        ServiceCollection services = new();
        services.AddLogging();
        new DownloadManagementModule().ConfigureServices(services, new ConfigurationBuilder().Build());

        // Override module defaults with test doubles (last registration wins).
        services.AddSingleton<ITorrentDownloader>(torrents);
        services.AddSingleton<IEventBus, EventBus>();
        services.AddSingleton<IEventStore, FakeEventStore>();
        services.AddSingleton<TimeProvider>(new TestTimeProvider(Start));
        services.AddSingleton<IOptionsMonitor<TorrentDownloaderConfig>>(new MutableOptionsMonitor<TorrentDownloaderConfig>(config));
        services.AddSingleton<IConnectionHealthProvider>(new FakeConnectionHealthProvider());
        services.AddSingleton<IDiskSpaceProvider>(disk ?? new FakeDiskSpaceProvider());

        return services.BuildServiceProvider();
    }

    [Test]
    public async Task TorrentHealthCheckJob_ExecutesDetector()
    {
        FakeTorrentDownloader torrents = new();
        torrents.TorrentsByFilter["all"] = [];
        using ServiceProvider provider = BuildProvider(torrents, TestConfigs.Health());

        TorrentHealthCheckJob job = new(provider.GetRequiredService<IServiceScopeFactory>());
        await job.Execute(CancellationToken.None);

        await Assert.That(torrents.FiltersRequested).Contains("all");
    }

    [Test]
    public async Task ErrorTorrentCleanupJob_ExecutesCleanup()
    {
        FakeTorrentDownloader torrents = new();
        torrents.TorrentsByFilter["error"] = [];
        using ServiceProvider provider = BuildProvider(torrents, TestConfigs.Errors());

        ErrorTorrentCleanupJob job = new(provider.GetRequiredService<IServiceScopeFactory>());
        await job.Execute(CancellationToken.None);

        await Assert.That(torrents.FiltersRequested).Contains("error");
    }

    [Test]
    public async Task SeedingLimitCleanupJob_ExecutesThroughScope()
    {
        FakeTorrentDownloader torrents = new();
        torrents.TorrentsByFilter["all"] = [TestConfigs.Torrent("h1", state: TorrentState.Seeding, progress: 1.0, ratio: 3.0, secondsSeeding: 3600)];
        using ServiceProvider provider = BuildProvider(torrents, TestConfigs.Seeding());

        SeedingLimitCleanupJob job = new(provider.GetRequiredService<IServiceScopeFactory>());
        await job.Execute(CancellationToken.None);

        await Assert.That(torrents.FiltersRequested).Contains("all");
        await Assert.That(torrents.Deleted.Count).IsEqualTo(1);
    }

    [Test]
    public async Task StorageCheckJob_ExecutesThroughScope()
    {
        FakeTorrentDownloader torrents = new();
        FakeDiskSpaceProvider disk = new() { Total = 1000, Used = 500 };
        using ServiceProvider provider = BuildProvider(torrents, TestConfigs.Storage(), disk);

        StorageCheckJob job = new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IOptionsMonitor<TorrentDownloaderConfig>>(),
            provider.GetRequiredService<IDiskSpaceProvider>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<IEventStore>(),
            provider.GetRequiredService<ILogger<StorageCheckJob>>());

        await job.Execute(CancellationToken.None);

        await Assert.That(disk.Calls).IsEqualTo(1);
        await Assert.That(torrents.PauseAllCount).IsEqualTo(0);
    }
}
