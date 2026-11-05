using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Stackarr.Core.Modules;
using Stackarr.Core.Notifications;
using Stackarr.Core.Services;
using Stackarr.Notifications.Discord;
using Stackarr.Ui;

namespace Stackarr.Notifications;

/// <summary>
/// Delivery transports for notifications. Kept separate from the core notification system so new
/// transports (Discord, generic webhook, later email/Telegram/Apprise) can be added or omitted
/// without touching core. Depends on <c>Stackarr.Core</c> and the auth abstraction (for linked
/// Discord identities); the module also owns the notification UI (the app-bar bell slot, All
/// Events page and notification settings), contributed through the <c>Stackarr.Ui</c> contracts.
/// </summary>
public class NotificationDeliveryModule : IStackarrModule
{
    public string ModuleName => "NotificationDelivery";
    public string ModuleDescription => "Discord (webhook and direct messages) and generic webhook notification delivery.";

    public IReadOnlyList<Service> GetServices() => [];

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddHttpClient();
        services.AddHttpClient(DiscordWebhookClient.ClientName);
        services.AddHttpClient(DiscordBotClient.ClientName);
        services.AddHttpClient(GenericWebhookNotificationChannel.ClientName);

        services.AddSingleton<DiscordWebhookClient>();
        services.AddSingleton<DiscordBotClient>();
        services.AddSingleton<DiscordNotificationChannel>();
        services.AddSingleton<INotificationChannel>(sp => sp.GetRequiredService<DiscordNotificationChannel>());
        services.AddSingleton<INotificationChannel, GenericWebhookNotificationChannel>();

        // The module owns the notifications UI: the event-log page, plus the app-bar bell which is
        // discovered from its [UiRegion] attribute.
        services.AddSingleton<INavigationProvider, NotificationsNavigation>();
        services.AddSingleton<IHeadAssetProvider, NotificationsHeadAssets>();
    }
}
