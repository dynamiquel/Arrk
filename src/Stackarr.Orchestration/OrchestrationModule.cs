using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Stackarr.Ai;
using Stackarr.Core.Modules;
using Stackarr.Core.Notifications;
using Stackarr.Core.Services;
using Stackarr.Orchestration.Jobs;
using Stackarr.Orchestration.Tools;
using Stackarr.Ui;

namespace Stackarr.Orchestration;

public class OrchestrationModule : IStackarrModule
{
    public string ModuleName => "Orchestration";
    public string ModuleDescription => "Provisioning, config sync, health, metrics and operational UI.";
    public IReadOnlyList<Service> GetServices() => [];

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<INotificationEventProvider, HealthNotificationEvents>();
        services.AddSingleton<INavigationProvider, OrchestrationNavigation>();
        services.AddSingleton<IHeadAssetProvider, OrchestrationHeadAssets>();
        services.AddSingleton<ITileProvider, OrchestrationTileProvider>();
        services.AddSingleton<DashboardSummaryCache>();
        services.AddSingleton<SchedulerViewModelService>();
        services.AddSingleton<IAssistantTool, OrchestrationAssistantTool>();
    }

    public void ConfigureJobs(IStackarrJobProvider jobs, IServiceProvider services)
    {
        jobs
            .RegisterScheduled<StatusUpdateJob>(
                j => j.Execute(CancellationToken.None),
                StatusUpdateJob.DefaultCron)
            .RegisterScheduled<MetricsCollectionJob>(
                j => j.Execute(CancellationToken.None),
                MetricsCollectionJob.DefaultCron)
            .RegisterScheduled<ServiceRecoveryJob>(
                j => j.Execute(CancellationToken.None),
                ServiceRecoveryJob.DefaultCron)
            .RegisterScheduled<ConfigSyncJob>(
                j => j.Execute(CancellationToken.None),
                ConfigSyncJob.DefaultCron)
            .RegisterScheduled<DockerOrchestrationJob>(
                j => j.Execute(CancellationToken.None),
                DockerOrchestrationJob.DefaultCron)
            .RegisterScheduled<OrphanCleanupJob>(
                j => j.Execute(CancellationToken.None),
                OrphanCleanupJob.DefaultCron);
    }
}