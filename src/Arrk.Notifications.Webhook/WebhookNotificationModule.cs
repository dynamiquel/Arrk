using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using FluentValidation;
using Arrk.Core.Configuration;
using Arrk.Core.Modules;
using Arrk.Core.Services;
using Arrk.Notifications;

namespace Arrk.Notifications.Webhook;

public sealed class WebhookNotificationModule : IArrkModule
{
    public string ModuleName => "WebhookNotifications";
    public string ModuleDescription => "Generic JSON webhook notification delivery.";

    public IReadOnlyList<Service> GetServices() => [];

    public IReadOnlyList<IConfigSectionProvider> GetConfigSections() =>
    [
        new ConfigSectionProvider<WebhookConfig>(WebhookConfig.SectionName,
            "Webhook notifications", icon: "Webhook", group: "Infrastructure", order: 82, groupOrder: 10)
    ];

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.ConfigureConfig<WebhookConfig>(configuration.GetSection(WebhookConfig.SectionName));
        services.AddSingleton<IValidator<WebhookConfig>, WebhookConfigValidator>();
        services.AddHttpClient(WebhookNotificationClient.ClientName);
        services.AddSingleton<INotificationClient, WebhookNotificationClient>();
    }
}
