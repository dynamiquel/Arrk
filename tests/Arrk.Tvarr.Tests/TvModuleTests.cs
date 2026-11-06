using Hangfire;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Arrk.Core;
using Arrk.Core.Configuration;
using Arrk.Core.Services;
using Arrk.Tvarr.Clients;
using Arrk.Tvarr.Configurations;
using Arrk.Tvarr.Jobs;
using Arrk.Tvarr.Services;

using Arrk.TestSupport;
namespace Arrk.Tvarr.Tests;

public class TvModuleTests
{
    static IConfiguration Config(Dictionary<string, string?>? values = null) =>
        new ConfigurationBuilder().AddInMemoryCollection(values ?? new()).Build();

    static ServiceCollection Services()
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton(new ArrkInstance(Config(new() { ["StackName"] = "test" })));
        return services;
    }

    // Hangfire's static RecurringJob API (used by TvModule for the cleanup job) needs storage.
    static void ConfigureHangfire()
    {
        GlobalConfiguration.Configuration.UseInMemoryStorage();
    }


    [Test]
    public async Task GetServices_ReturnsTvService()
    {
        IReadOnlyList<Service> services = new TvModule().GetServices();

        await Assert.That(services.Count).IsEqualTo(1);
        await Assert.That(services[0].Definition.Key).IsEqualTo("tv");
    }

    [Test]
    public async Task GetConfigSections_RegistersTvSection()
    {
        IReadOnlyList<IConfigSectionProvider> sections = new TvModule().GetConfigSections();

        await Assert.That(sections.Single().SectionPath).IsEqualTo("services:tv");
        await Assert.That(sections.Single().ConfigType).IsEqualTo(typeof(TvConfig));
    }

    [Test]
    public async Task GetDependencies_IncludeRadarrAndSonarr()
    {
        var deps = new TvModule().GetDependencies();

        await Assert.That(deps.Any(d => d.Name == "Radarr")).IsTrue();
        await Assert.That(deps.Any(d => d.Name == "Sonarr")).IsTrue();
    }

    [Test]
    public async Task ConfigureServices_RegistersClientsAndServices()
    {
        ServiceCollection services = Services();

        new TvModule().ConfigureServices(services, Config());
        using ServiceProvider provider = services.BuildServiceProvider();

        await Assert.That(provider.GetService<RadarrClient>()).IsNotNull();
        await Assert.That(provider.GetService<SonarrClient>()).IsNotNull();
        await Assert.That(provider.GetService<RadarrService>()).IsNotNull();
        await Assert.That(provider.GetService<SonarrService>()).IsNotNull();
        await Assert.That(provider.GetService<TorrentCleanupJob>()).IsNotNull();
        await Assert.That(services.Any(descriptor => descriptor.ServiceType == typeof(TvUploadStore))).IsTrue();
        await Assert.That(services.Any(descriptor => descriptor.ServiceType == typeof(TvImportService))).IsTrue();
        await Assert.That(provider.GetServices<IModuleEntityConfiguration>())
            .Contains(configuration => configuration is PendingTorrentCleanupConfiguration);
        await Assert.That(provider.GetServices<IModuleEntityConfiguration>())
            .Contains(configuration => configuration is TvUploadRecordConfiguration);
    }

    [Test]
    public async Task ConfigureServices_ClientBaseUrlsUseInstanceNames()
    {
        ServiceCollection services = Services();

        new TvModule().ConfigureServices(services, Config());
        using ServiceProvider provider = services.BuildServiceProvider();

        RadarrClient radarr = provider.GetRequiredService<RadarrClient>();
        SonarrClient sonarr = provider.GetRequiredService<SonarrClient>();

        Uri radarrBase = GetBaseAddress(radarr);
        await Assert.That(radarrBase.Host).IsEqualTo("stackarr-test-radarr");
        await Assert.That(radarrBase.Port).IsEqualTo(7878);
        await Assert.That(radarrBase.AbsolutePath).IsEqualTo("/radarr/");

        Uri sonarrBase = GetBaseAddress(sonarr);
        await Assert.That(sonarrBase.Host).IsEqualTo("stackarr-test-sonarr");
        await Assert.That(sonarrBase.Port).IsEqualTo(8989);
        await Assert.That(sonarrBase.AbsolutePath).IsEqualTo("/sonarr/");
    }

    static Uri GetBaseAddress(ArrClient client)
    {
        var field = typeof(ArrClient).GetField("Http",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var http = (HttpClient)field.GetValue(client)!;
        return http.BaseAddress!;
    }

    [Test]
    public async Task ConfigureServices_BindsTvConfig()
    {
        ServiceCollection services = Services();
        IConfiguration config = Config(new()
        {
            ["services:tv:qualityProfileName"] = "UHD",
            ["services:tv:enabled"] = "false",
            ["services:tv:failedDownloads:retriggerSearch"] = "false"
        });

        new TvModule().ConfigureServices(services, config);
        using ServiceProvider provider = services.BuildServiceProvider();

        TvConfig bound = provider.GetRequiredService<IOptions<TvConfig>>().Value;
        await Assert.That(bound.QualityProfileName).IsEqualTo("UHD");
        await Assert.That(bound.Enabled).IsFalse();
        await Assert.That(bound.FailedDownloads.RetriggerSearch).IsFalse();
    }

    [Test]
    public async Task ConfigureJobs_RegistersSevenScheduledJobs()
    {
        ConfigureHangfire();
        FakeJobProvider provider = new();

        new TvModule().ConfigureJobs(provider, new ServiceCollection().BuildServiceProvider());

        await Assert.That(provider.Scheduled.Count).IsEqualTo(7);
        await Assert.That(provider.Scheduled.Select(s => s.JobType)).Contains(typeof(TvUploadCleanupJob));
        await Assert.That(provider.Scheduled.Select(s => s.JobType)).Contains(typeof(RootFolderSyncJob));
        await Assert.That(provider.Scheduled.Select(s => s.JobType)).Contains(typeof(QualityProfileSyncJob));
        await Assert.That(provider.Scheduled.Select(s => s.JobType)).Contains(typeof(DownloadClientSyncJob));
        await Assert.That(provider.Scheduled.Select(s => s.JobType)).Contains(typeof(ProwlarrAppSyncJob));
        await Assert.That(provider.Scheduled.Select(s => s.JobType)).Contains(typeof(FailedDownloadHandlingSyncJob));
        await Assert.That(provider.Scheduled.Select(s => s.JobType)).Contains(typeof(MissingContentSearchJob));
        await Assert.That(provider.Scheduled.Single(s => s.JobType == typeof(RootFolderSyncJob)).Cron)
            .IsEqualTo(RootFolderSyncJob.DefaultCron);
        await Assert.That(provider.Scheduled.Single(s => s.JobType == typeof(FailedDownloadHandlingSyncJob)).Cron)
            .IsEqualTo(FailedDownloadHandlingSyncJob.DefaultCron);
        await Assert.That(provider.Scheduled.Single(s => s.JobType == typeof(MissingContentSearchJob)).Cron)
            .IsEqualTo(MissingContentSearchJob.DefaultCron);
        await Assert.That(provider.OptionsChangeJobs.Count).IsEqualTo(5);
    }
}
