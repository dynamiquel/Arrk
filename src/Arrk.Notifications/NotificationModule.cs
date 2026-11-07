using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Arrk.Core.Documentation;
using Arrk.Core.Modules;
using Arrk.Core.Services;
using Arrk.Ui;

namespace Arrk.Notifications;

/// <summary>
/// Notification policy, audience resolution, inbox, and UI. Transports register independently;
/// this module never references a client implementation or its configuration.
/// </summary>
public class NotificationModule : IArrkModule
{
    public string ModuleName => "Notifications";
    public string ModuleDescription => "Notification audiences, topic policy, subscriptions, and per-user inbox.";

    public IReadOnlyList<Service> GetServices() => [];

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDocumentation("notifications", typeof(NotificationModule).Assembly);
        services.Configure<NotificationConfig>(configuration.GetSection("notifications"));
        services.AddSingleton<NotificationEventRegistry>();
        services.AddSingleton<NotificationSubscriptionService>();
        services.AddSingleton<INotificationSubscriptionService>(sp => sp.GetRequiredService<NotificationSubscriptionService>());
        services.AddSingleton<UserNotificationChannelService>();
        services.AddSingleton<NotificationStackInfo>();
        services.AddSingleton<NotificationAudienceResolver>();
        services.AddSingleton<INotificationService, NotificationService>();
        services.AddSingleton<NotificationPurgeJob>();

        // The module owns the notifications UI: the event-log page, plus the app-bar bell which is
        // discovered from its [UiRegion] attribute.
        services.AddSingleton<INavigationProvider, NotificationsNavigation>();
        services.AddSingleton<IHeadAssetProvider, NotificationsHeadAssets>();
    }

    public void ConfigureJobs(IArrkJobProvider jobs, IServiceProvider services) =>
        jobs.RegisterScheduled<NotificationPurgeJob>(j => j.Execute(CancellationToken.None), NotificationPurgeJob.DefaultCron);
}
