using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Stackarr.Core;
using Stackarr.Core.Lifecycle;
using Stackarr.Core.Models;
using Stackarr.Core.Modules;
using Stackarr.Core.Notifications;
using Stackarr.Core.Services;
using Stackarr.Ui;

namespace Stackarr.MediaLifecycle;

/// <summary>
/// Owns staleness evaluation, the hourly cleanup job, notifications and the admin cleanup page.
/// Library modules opt in with <see cref="IManagedMediaProvider"/>; everyone else reads statuses
/// through Core's <see cref="StaleMediaService"/>. The module never references a library module.
/// </summary>
public class MediaLifecycleModule : IStackarrModule
{
    public string ModuleName => "Media lifecycle";
    public string ModuleDescription => "Watch/listen based staleness tracking and media cleanup.";

    public IReadOnlyList<Service> GetServices() => [];

    public IReadOnlyList<ModuleDependency> GetDependencies() => [];

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IModuleEntityConfiguration, LifecycleItemStateConfiguration>();
        services.AddSingleton<MediaLifecycleService>();
        services.AddSingleton<IStaleMediaProvider>(sp => sp.GetRequiredService<MediaLifecycleService>());
        services.AddSingleton<INotificationEventProvider, MediaLifecycleNotificationEvents>();
        services.AddSingleton<INavigationProvider, MediaLifecycleNavigation>();
        services.AddSingleton<ITileProvider, MediaLifecycleTileProvider>();
        services.AddSingleton<IHeadAssetProvider, MediaLifecycleHeadAssets>();
        services.AddScoped<StaleCleanupJob>();
    }

    public void ConfigureJobs(IStackarrJobProvider jobs, IServiceProvider services)
    {
        jobs.RegisterScheduled<StaleCleanupJob>(
            j => j.Execute(CancellationToken.None),
            StaleCleanupJob.DefaultCron);
    }
}
