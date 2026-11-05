using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Stackarr.Core;
using Stackarr.Core.Services;
using Stackarr.DownloadManagement.Jobs;

using Stackarr.TestSupport;
namespace Stackarr.DownloadManagement.Tests;

public class DownloadManagementModuleTests
{
    static IConfiguration Config() => new ConfigurationBuilder().Build();


    [Test]
    public async Task GetServices_ReturnsNoServices()
    {
        await Assert.That(new DownloadManagementModule().GetServices()).IsEmpty();
    }

    [Test]
    public async Task ConfigureServices_RegistersCoreServices()
    {
        ServiceCollection services = new();

        new DownloadManagementModule().ConfigureServices(services, Config());

        await Assert.That(services.Any(d => d.ServiceType == typeof(TorrentHealthDetector))).IsTrue();
        await Assert.That(services.Any(d => d.ServiceType == typeof(TorrentPenaltyTracker))).IsTrue();
        await Assert.That(services.Any(d => d.ServiceType == typeof(ITorrentPenaltyService))).IsTrue();
        await Assert.That(services.Any(d => d.ServiceType == typeof(ErrorTorrentCleanup))).IsTrue();
        await Assert.That(services.Any(d => d.ServiceType == typeof(DownloadEventMonitor))).IsTrue();
        await Assert.That(services.Any(d => d.ServiceType == typeof(IDiskSpaceProvider))).IsTrue();
        await Assert.That(services.Any(d => d.ServiceType == typeof(StorageSpaceService))).IsTrue();
    }

    [Test]
    public async Task ConfigureServices_WithCore_ResolvesDiskSpaceProviderAndTimeProvider()
    {
        ServiceCollection services = new();
        services.AddStackarrCore();
        new DownloadManagementModule().ConfigureServices(services, Config());
        using ServiceProvider provider = services.BuildServiceProvider();

        await Assert.That(provider.GetService<IDiskSpaceProvider>()).IsNotNull();
        await Assert.That(provider.GetService<TimeProvider>()).IsSameReferenceAs(TimeProvider.System);
    }

    [Test]
    public async Task ConfigureJobs_RegistersAllJobs()
    {
        FakeJobProvider provider = new();

        new DownloadManagementModule().ConfigureJobs(provider, new ServiceCollection().BuildServiceProvider());

        await Assert.That(provider.Scheduled.Count).IsEqualTo(5);
        await Assert.That(provider.Scheduled.Select(s => s.JobType)).Contains(typeof(StorageCheckJob));
        await Assert.That(provider.Scheduled.Select(s => s.JobType)).Contains(typeof(ErrorTorrentCleanupJob));
        await Assert.That(provider.Scheduled.Select(s => s.JobType)).Contains(typeof(TorrentHealthCheckJob));
        await Assert.That(provider.Scheduled.Select(s => s.JobType)).Contains(typeof(SeedingLimitCleanupJob));
        await Assert.That(provider.Scheduled.Select(s => s.JobType)).Contains(typeof(DownloadEventMonitorJob));
        await Assert.That(provider.Scheduled.Single(s => s.JobType == typeof(StorageCheckJob)).Cron)
            .IsEqualTo(StorageCheckJob.DefaultCron);
    }
}
