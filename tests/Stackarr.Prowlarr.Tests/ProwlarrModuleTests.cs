using Hangfire;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Stackarr.Core;
using Stackarr.Core.Configuration;
using Stackarr.Core.Modules;
using Stackarr.Core.Services;
using Stackarr.Prowlarr;
using Stackarr.Prowlarr.Configuration;
using Stackarr.Prowlarr.Jobs;
using Stackarr.TestSupport;

namespace Stackarr.Prowlarr.Tests;

public class ProwlarrModuleTests
{
    static IConfiguration Config(Dictionary<string, string?>? values = null) =>
        new ConfigurationBuilder().AddInMemoryCollection(values ?? new()).Build();

    static ServiceCollection Services()
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton(new StackarrInstance(Config(new() { ["StackName"] = "test" })));
        return services;
    }


    [Test]
    public async Task GetServices_ReturnsIndexerService()
    {
        IReadOnlyList<Service> services = new ProwlarrModule().GetServices();

        await Assert.That(services.Count).IsEqualTo(1);
        await Assert.That(services[0].Definition.Key).IsEqualTo("indexer");
    }

    [Test]
    public async Task GetConfigSections_RegistersIndexersSection()
    {
        IConfigSectionProvider section = new ProwlarrModule().GetConfigSections().Single();

        await Assert.That(section.SectionPath).IsEqualTo("indexers");
        await Assert.That(section.ConfigType).IsEqualTo(typeof(ProwlarrConfig));
    }

    [Test]
    public async Task GetDependencies_IncludeProwlarr()
    {
        IReadOnlyList<ModuleDependency> deps = new ProwlarrModule().GetDependencies();

        ModuleDependency prowlarr = deps.Single(d => d.Name == "Prowlarr");
        await Assert.That(prowlarr.Url).Contains("github.com/Prowlarr/Prowlarr");
    }

    [Test]
    public async Task ConfigureServices_RegistersClientAndService_WithInstanceUrl()
    {
        ServiceCollection services = Services();

        new ProwlarrModule().ConfigureServices(services, Config());
        using ServiceProvider provider = services.BuildServiceProvider();

        ProwlarrClient client = provider.GetRequiredService<ProwlarrClient>();
        await Assert.That(client).IsNotNull();
        await Assert.That(provider.GetRequiredService<ProwlarrService>()).IsNotNull();
    }

    [Test]
    public async Task ConfigureServices_BindsIndexersSection()
    {
        ServiceCollection services = Services();
        IConfiguration config = Config(new() { ["indexers:entries:0:name"] = "TPB" });

        new ProwlarrModule().ConfigureServices(services, config);
        using ServiceProvider provider = services.BuildServiceProvider();

        ProwlarrConfig bound = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<ProwlarrConfig>>().Value;
        await Assert.That(bound.Entries.Single().Name).IsEqualTo("TPB");
    }

    [Test]
    public async Task ConfigureJobs_RegistersIndexerSyncJob()
    {
        GlobalConfiguration.Configuration.UseInMemoryStorage();
        FakeJobProvider jobs = new();

        new ProwlarrModule().ConfigureJobs(jobs, Services().BuildServiceProvider());

        await Assert.That(jobs.Scheduled.Single().JobType).IsEqualTo(typeof(IndexerSyncJob));
        await Assert.That(jobs.Scheduled.Single().Cron).IsEqualTo(IndexerSyncJob.DefaultCron);
        await Assert.That(jobs.OptionsChangeJobs.Single()).IsEqualTo(typeof(IndexerSyncJob));
    }
}
