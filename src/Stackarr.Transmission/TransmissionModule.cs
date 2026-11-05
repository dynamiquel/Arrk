using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Stackarr.Core;
using Stackarr.Core.Configuration;
using Stackarr.Core.Models;
using Stackarr.Core.Modules;
using Stackarr.Core.Services;

namespace Stackarr.Transmission;

public class TransmissionModule : IStackarrModule
{
    public string ModuleName => "TorrentDownloader";
    public string ModuleDescription => "Torrent downloader, limits and seeding lifecycle.";

    public IReadOnlyList<Service> GetServices()
    {
        return [new(new TransmissionServiceDefinition())];
    }

    public IReadOnlyList<IConfigSectionProvider> GetConfigSections() =>
        [new ConfigSectionProvider<TorrentDownloaderConfig>("services:torrentDownloader", "Downloads",
            icon: "Download", group: "Services", order: 40, groupOrder: 20)];

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<TorrentDownloaderConfig>(configuration.GetSection("services:torrentDownloader"));

        services.AddSingleton<TransmissionClient>(sp =>
        {
            StackarrInstance stackarr = sp.GetRequiredService<StackarrInstance>();
            IConfiguration config = sp.GetRequiredService<IConfiguration>();
            // Resolve per request so the client follows VPN routing toggles at runtime.
            return new(
                () => $"http://{stackarr.ResolveServiceHost(stackarr.GetContainerName("transmission"), config.IsTorrentVpnRouted())}:9091",
                TransmissionServiceDefinition.DesiredUsername,
                TransmissionServiceDefinition.DesiredPassword);
        });

        services.AddSingleton<ITorrentDownloader, TransmissionService>();
    }

    public IReadOnlyList<ModuleDependency> GetDependencies() =>
    [
        new("Transmission", "MIT", "Transmission", "https://github.com/transmission/transmission", "Fast, easy, and free BitTorrent client"),
    ];

    public void ConfigureJobs(IStackarrJobProvider jobs, IServiceProvider services)
    {
        jobs
            .RegisterScheduledAndOptionsChange<TransmissionLimitsJob, TorrentDownloaderConfig>(
                j => j.Execute(CancellationToken.None),
                TransmissionLimitsJob.DefaultCron);
    }
}
