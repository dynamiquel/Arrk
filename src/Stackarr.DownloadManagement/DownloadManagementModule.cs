using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Stackarr.Ai;
using Stackarr.Core.Modules;
using Stackarr.Core.Notifications;
using Stackarr.Core.Services;
using Stackarr.DownloadManagement.Jobs;
using Stackarr.DownloadManagement.Tools;
using Stackarr.Ui;

namespace Stackarr.DownloadManagement;

public class DownloadManagementModule : IStackarrModule
{
    public string ModuleName => "DownloadManagement";
    public string ModuleDescription => "Torrent management, cleanup and the unified Downloads page.";

    public IReadOnlyList<Core.Services.Service> GetServices() => [];

    public void ConfigureServices(
        IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddSingleton<TorrentPenaltyTracker>();
        services.AddSingleton<ITorrentPenaltyService>(sp => sp.GetRequiredService<TorrentPenaltyTracker>());
        services.AddSingleton<TorrentHealthDetector>();
        services.AddSingleton<ErrorTorrentCleanup>();
        services.AddSingleton<SeedingLimitCleanup>();
        services.AddSingleton<DownloadEventMonitor>();
        services.AddSingleton<IDiskSpaceProvider, DockerDiskSpaceProvider>();
        services.AddSingleton<StorageSpaceService>();
        services.AddSingleton<DownloadSummaryCache>();
        services.AddSingleton<ITileProvider, DownloadManagementTileProvider>();
        services.AddSingleton<INotificationEventProvider, StorageNotificationEvents>();
        services.AddSingleton<INotificationEventProvider, DownloadNotificationEvents>();

        // The module owns the unified Downloads page.
        services.AddSingleton<INavigationProvider, DownloadManagementNavigation>();
        services.AddSingleton<IHeadAssetProvider, DownloadManagementHeadAssets>();
        services.AddSingleton<IAssistantTool, DownloadsAssistantTool>();
    }

    public void ConfigureJobs(IStackarrJobProvider jobs, IServiceProvider services)
    {
        jobs
            .RegisterScheduled<StorageCheckJob>(
                j => j.Execute(CancellationToken.None),
                StorageCheckJob.DefaultCron)
            .RegisterScheduled<ErrorTorrentCleanupJob>(
                j => j.Execute(CancellationToken.None),
                ErrorTorrentCleanupJob.DefaultCron)
            .RegisterScheduled<TorrentHealthCheckJob>(
                j => j.Execute(CancellationToken.None),
                TorrentHealthCheckJob.DefaultCron)
            .RegisterScheduled<SeedingLimitCleanupJob>(
                j => j.Execute(CancellationToken.None),
                SeedingLimitCleanupJob.DefaultCron)
            .RegisterScheduled<DownloadEventMonitorJob>(
                j => j.Execute(CancellationToken.None),
                DownloadEventMonitorJob.DefaultCron);
    }
}
