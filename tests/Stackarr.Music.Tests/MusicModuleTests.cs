using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Stackarr.Core.Notifications;
using Stackarr.Core.Requests;
using Stackarr.Core.Services;
using Stackarr.Music.Services;

namespace Stackarr.Music.Tests;

/// <summary>
/// Covers the module's DI surface. In particular the concrete <see cref="MusicReleaseProvider"/> must
/// be registered (the album dialog injects the concrete type, not just <see cref="ReleaseProvider"/>).
/// </summary>
public class MusicModuleTests
{
    static IServiceCollection Configure()
    {
        ServiceCollection services = new();
        IConfiguration configuration = new ConfigurationBuilder().Build();
        new MusicModule().ConfigureServices(services, configuration);
        return services;
    }

    [Test]
    public async Task ConfigureServices_RegistersConcreteMusicReleaseProvider()
    {
        IServiceCollection services = Configure();

        await Assert.That(services.Any(d => d.ServiceType == typeof(MusicReleaseProvider))).IsTrue();
        await Assert.That(services.Any(d => d.ServiceType == typeof(ReleaseProvider))).IsTrue();
    }

    [Test]
    public async Task ConfigureServices_RegistersMusicRequestHandler_AndNotificationEvents()
    {
        IServiceCollection services = Configure();

        await Assert.That(services.Any(d => d.ServiceType == typeof(IMediaRequestHandler))).IsTrue();
        await Assert.That(services.Any(d => d.ServiceType == typeof(INotificationEventProvider))).IsTrue();
        await Assert.That(services.Any(d => d.ServiceType == typeof(MusicLibrary))).IsTrue();
        await Assert.That(services.Any(d => d.ServiceType == typeof(MusicCatalogService))).IsTrue();
        await Assert.That(services.Any(d => d.ServiceType == typeof(MusicAcquisitionService))).IsTrue();
    }

    [Test]
    public async Task Module_HasNoContainerService()
    {
        await Assert.That(new MusicModule().GetServices()).IsEmpty();
    }

    [Test]
    public async Task ConfigureJobs_PutsImportAndFailureCheck_OnTheMusicQueue()
    {
        Stackarr.TestSupport.FakeJobProvider jobs = new();
        new MusicModule().ConfigureJobs(jobs, new ServiceCollection().BuildServiceProvider());

        (string JobName, Type JobType, string Cron, string? Queue) import =
            jobs.Scheduled.Single(s => s.JobType == typeof(Jobs.MusicDownloadImportJob));
        (string JobName, Type JobType, string Cron, string? Queue) failure =
            jobs.Scheduled.Single(s => s.JobType == typeof(Jobs.SoulseekFailureCheckJob));

        await Assert.That(import.Queue).IsEqualTo(Stackarr.Core.Modules.StackarrHangfireJobProvider.MusicQueue);
        await Assert.That(failure.Queue).IsEqualTo(Stackarr.Core.Modules.StackarrHangfireJobProvider.MusicQueue);
    }
}
