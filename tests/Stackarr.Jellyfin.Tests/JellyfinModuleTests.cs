using Hangfire;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Stackarr.Core;
using Stackarr.Core.Configuration;
using Stackarr.Core.Modules;
using Stackarr.Core.Services;
using Stackarr.Jellyfin;
using Stackarr.Jellyfin.Jobs;
using Stackarr.TestSupport;

namespace Stackarr.Jellyfin.Tests;

public class JellyfinModuleTests
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
    public async Task GetServices_ReturnsJellyfinService()
    {
        IReadOnlyList<Service> services = new JellyfinModule().GetServices();

        await Assert.That(services.Count).IsEqualTo(1);
        await Assert.That(services[0].Definition.Key).IsEqualTo("jellyfin");
    }

    [Test]
    public async Task GetConfigSections_RegistersJellyfinSection()
    {
        IConfigSectionProvider section = new JellyfinModule().GetConfigSections().Single();

        await Assert.That(section.SectionPath).IsEqualTo("services:jellyfin");
        await Assert.That(section.ConfigType).IsEqualTo(typeof(JellyfinConfig));
    }

    [Test]
    public async Task GetDependencies_IncludeJellyfin()
    {
        IReadOnlyList<ModuleDependency> deps = new JellyfinModule().GetDependencies();

        ModuleDependency jellyfin = deps.Single(d => d.Name == "Jellyfin");
        await Assert.That(jellyfin.Url).Contains("github.com/jellyfin/jellyfin");
    }

    [Test]
    public async Task ConfigureServices_RegistersClientAndServices()
    {
        ServiceCollection services = Services();

        new JellyfinModule().ConfigureServices(services, Config());
        using ServiceProvider provider = services.BuildServiceProvider();

        await Assert.That(provider.GetRequiredService<JellyfinClient>()).IsNotNull();
        await Assert.That(provider.GetRequiredService<JellyfinService>()).IsNotNull();
        await Assert.That(provider.GetRequiredService<JellyfinLinkService>()).IsNotNull();
    }

    [Test]
    public async Task ConfigureServices_ClientPointsAtInternalContainerWithBaseUrl()
    {
        ServiceCollection services = Services();

        new JellyfinModule().ConfigureServices(services, Config());
        using ServiceProvider provider = services.BuildServiceProvider();

        JellyfinClient client = provider.GetRequiredService<JellyfinClient>();
        await Assert.That(client.BaseAddress).IsEqualTo($"http://stackarr-test-jellyfin:{JellyfinModule.HttpPort}/jellyfin");
    }

    [Test]
    public async Task ConfigureServices_BindsJellyfinSection()
    {
        ServiceCollection services = Services();
        IConfiguration config = Config(new() { ["services:jellyfin:serverName"] = "Media" });

        new JellyfinModule().ConfigureServices(services, config);
        using ServiceProvider provider = services.BuildServiceProvider();

        JellyfinConfig bound = provider.GetRequiredService<IOptions<JellyfinConfig>>().Value;
        await Assert.That(bound.ServerName).IsEqualTo("Media");
    }

    [Test]
    public async Task ConfigureHostedServices_RegistersInitialiser()
    {
        ServiceCollection services = Services();

        new JellyfinModule().ConfigureHostedServices(services);

        await Assert.That(services.Any(d =>
            d.ServiceType == typeof(IHostedService) &&
            d.ImplementationType == typeof(JellyfinInitialiser))).IsTrue();
    }

    [Test]
    public async Task ConfigureJobs_RegistersConfigSyncAndLibraryScanJobs()
    {
        GlobalConfiguration.Configuration.UseInMemoryStorage();
        FakeJobProvider jobs = new();
        ServiceCollection services = Services();
        IConfiguration config = Config();
        new JellyfinModule().ConfigureServices(services, config);
        using ServiceProvider provider = services.BuildServiceProvider();

        new JellyfinModule().ConfigureJobs(jobs, provider);

        await Assert.That(jobs.Scheduled.Count).IsEqualTo(2);
        await Assert.That(jobs.Scheduled.Any(j =>
            j.JobType == typeof(JellyfinConfigSyncJob) && j.Cron == JellyfinConfigSyncJob.DefaultCron)).IsTrue();
        await Assert.That(jobs.Scheduled.Any(j =>
            j.JobType == typeof(JellyfinLibraryScanJob) && j.Cron == JellyfinLibraryScanJob.DefaultCron)).IsTrue();
        await Assert.That(jobs.OptionsChangeJobs.Single()).IsEqualTo(typeof(JellyfinConfigSyncJob));
    }
}
