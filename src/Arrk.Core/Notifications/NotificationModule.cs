using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Arrk.Core.Configuration;
using Arrk.Core.Modules;
using Arrk.Core.Services;

namespace Arrk.Core.Notifications;

/// <summary>
/// Core notification infrastructure: binds <see cref="NotificationConfig"/>, registers the inbox
/// dispatcher, and schedules retention cleanup. Delivery transports (Discord, webhook, ...) are
/// contributed separately by modules, so notifications work in-app even with none installed.
/// </summary>
public class NotificationModule : IArrkModule
{
    public string ModuleName => "Notifications";
    public string ModuleDescription => "Durable event log, notification policy and the in-app inbox.";

    public IReadOnlyList<Service> GetServices() => [];

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<NotificationConfig>(configuration.GetSection("notifications"));
        services.AddSingleton<INotificationService, NotificationService>();
        services.AddSingleton<NotificationPurgeJob>();
    }

    public void ConfigureJobs(IArrkJobProvider jobs, IServiceProvider services)
    {
        jobs.RegisterScheduled<NotificationPurgeJob>(
            j => j.Execute(CancellationToken.None),
            NotificationPurgeJob.DefaultCron);
    }
}
