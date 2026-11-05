using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Stackarr.Core;
using Stackarr.Core.Configuration;
using Stackarr.Core.Modules;
using Stackarr.Core.Services;
using Stackarr.Prowlarr.Configuration;
using Stackarr.Prowlarr.Jobs;

namespace Stackarr.Prowlarr;

public class ProwlarrModule : IStackarrModule
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
            StackarrInstance stackarr = sp.GetRequiredService<StackarrInstance>();
            string url = $"http://{stackarr.GetContainerName("prowlarr")}:9696/prowlarr";
            return new(url, ServiceApiKey);
        });

        services.AddSingleton<ProwlarrService>();
    }

    public IReadOnlyList<ModuleDependency> GetDependencies() =>
    [
        new("Prowlarr", "GPL-3.0", "Prowlarr", "https://github.com/Prowlarr/Prowlarr", "Indexer manager/proxy for Usenet and BitTorrent"),
    ];

    public void ConfigureJobs(IStackarrJobProvider jobs, IServiceProvider services)
    {
        jobs
            .RegisterScheduledAndOptionsChange<IndexerSyncJob, ProwlarrConfig>(
                j => j.Execute(CancellationToken.None),
                IndexerSyncJob.DefaultCron);
    }
}
