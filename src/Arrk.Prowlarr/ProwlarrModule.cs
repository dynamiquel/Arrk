using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Arrk.Core;
using Arrk.Core.Configuration;
using Arrk.Core.Modules;
using Arrk.Core.Services;
using Arrk.Prowlarr.Configuration;
using Arrk.Prowlarr.Jobs;

namespace Arrk.Prowlarr;

public class ProwlarrModule : IArrkModule
{
    public const string ServiceApiKey = "stackarr";

    public string ModuleName => "Indexer";
    public string ModuleDescription => "Indexer management and Prowlarr app sync.";

    public IReadOnlyList<Service> GetServices()
    {
        return [new(new ProwlarrServiceDefinition())];
    }

    public IReadOnlyList<IConfigSectionProvider> GetConfigSections() =>
        [new ConfigSectionProvider<ProwlarrConfig>("indexers", "Indexers",
            icon: "TravelExplore", group: "Infrastructure", order: 70, groupOrder: 10)];

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ProwlarrConfig>(configuration.GetSection("indexers"));

        services.AddSingleton<ProwlarrClient>(sp =>
        {
            ArrkInstance arrk = sp.GetRequiredService<ArrkInstance>();
            string url = $"http://{arrk.GetContainerName("prowlarr")}:9696/prowlarr";
            return new(url, ServiceApiKey);
        });

        services.AddSingleton<ProwlarrService>();
    }

    public IReadOnlyList<ModuleDependency> GetDependencies() =>
    [
        new("Prowlarr", "GPL-3.0", "Prowlarr", "https://github.com/Prowlarr/Prowlarr", "Indexer manager/proxy for Usenet and BitTorrent"),
    ];

    public void ConfigureJobs(IArrkJobProvider jobs, IServiceProvider services)
    {
        jobs
            .RegisterScheduledAndOptionsChange<IndexerSyncJob, ProwlarrConfig>(
                j => j.Execute(CancellationToken.None),
                IndexerSyncJob.DefaultCron);
    }
}
