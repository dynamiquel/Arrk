using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Stackarr.Core;
using Stackarr.Core.Configuration;
using Stackarr.Core.Services;

using Stackarr.TestSupport;
namespace Stackarr.Transmission.Tests;

public class TransmissionModuleTests
{
    static IConfiguration Config(Dictionary<string, string?>? values = null) =>
        new ConfigurationBuilder().AddInMemoryCollection(values ?? new()).Build();


    [Test]
    public async Task GetServices_ReturnsTorrentDownloaderService()
    {
        IReadOnlyList<Service> services = new TransmissionModule().GetServices();

        await Assert.That(services.Count).IsEqualTo(1);
        await Assert.That(services[0].Definition.Key).IsEqualTo("torrent_downloader");
    }

    [Test]
    public async Task GetConfigSections_RegistersCamelCaseSection()
    {
        IReadOnlyList<IConfigSectionProvider> sections = new TransmissionModule().GetConfigSections();

        await Assert.That(sections.Count).IsEqualTo(1);
        await Assert.That(sections[0].SectionPath).IsEqualTo("services:torrentDownloader");
        await Assert.That(sections[0].ConfigType).IsEqualTo(typeof(TorrentDownloaderConfig));
    }

    [Test]
    public async Task GetDependencies_IncludesTransmission()
    {
        await Assert.That(new TransmissionModule().GetDependencies().Any(d => d.Name == "Transmission")).IsTrue();
    }

    [Test]
    public async Task ConfigureServices_RegistersClientAndDownloader()
    {
        ServiceCollection services = new();
        services.AddLogging();
        IConfiguration config = Config();
        services.AddSingleton(config);
        services.AddSingleton(new StackarrInstance(Config(new() { ["StackName"] = "test" })));

        new TransmissionModule().ConfigureServices(services, config);
        using ServiceProvider provider = services.BuildServiceProvider();

        await Assert.That(provider.GetService<TransmissionClient>()).IsNotNull();
        await Assert.That(provider.GetService<ITorrentDownloader>()).IsNotNull();
        await Assert.That(provider.GetService<ITorrentDownloader>()).IsTypeOf<TransmissionService>();
    }

    [Test]
    public async Task ConfigureServices_BindsCamelCaseConfig()
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton(new StackarrInstance(Config(new() { ["StackName"] = "test" })));
        IConfiguration config = Config(new()
        {
            ["services:torrentDownloader:limits:maxActiveDownloads"] = "3",
            ["services:torrentDownloader:speed:uploadMbps"] = "7"
        });
        services.AddSingleton(config);

        new TransmissionModule().ConfigureServices(services, config);
        using ServiceProvider provider = services.BuildServiceProvider();

        IOptions<TorrentDownloaderConfig> bound =
            provider.GetRequiredService<IOptions<TorrentDownloaderConfig>>();
        await Assert.That(bound.Value.Limits.MaxActiveDownloads).IsEqualTo(3);
        await Assert.That(bound.Value.Speed.UploadMbps).IsEqualTo(7);
    }

    [Test]
    public async Task ConfigureJobs_RegistersScheduledAndOptionsChangeJob()
    {
        FakeJobProvider provider = new();

        new TransmissionModule().ConfigureJobs(provider, new ServiceCollection().BuildServiceProvider());

        await Assert.That(provider.Scheduled.Count).IsEqualTo(1);
        await Assert.That(provider.Scheduled[0].JobType).IsEqualTo(typeof(TransmissionLimitsJob));
        await Assert.That(provider.Scheduled[0].Cron).IsEqualTo(TransmissionLimitsJob.DefaultCron);
        await Assert.That(provider.OptionsChangeJobs).Contains(typeof(TransmissionLimitsJob));
    }
}
