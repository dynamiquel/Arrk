using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using FluentValidation;
using Arrk.Core.Configuration;
using Arrk.Core.Modules;
using Arrk.Core.Services;
using Arrk.Notifications;

namespace Arrk.Notifications.Discord;

public sealed class DiscordNotificationModule : IArrkModule
{
    public string ModuleName => "DiscordNotifications";
    public string ModuleDescription => "Discord webhook and per-user bot direct-message notifications.";

    public IReadOnlyList<Service> GetServices() => [];

    public IReadOnlyList<IConfigSectionProvider> GetConfigSections() =>
    [
        new ConfigSectionProvider<DiscordNotificationConfig>(DiscordNotificationConfig.SectionName,
            "Discord notifications", icon: "Notifications", group: "Infrastructure", order: 81, groupOrder: 10)
    ];

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<DiscordNotificationConfig>(configuration.GetSection(DiscordNotificationConfig.SectionName));
        services.AddSingleton<IValidator<DiscordNotificationConfig>, DiscordNotificationConfigValidator>();
        services.AddHttpClient(DiscordWebhookClient.ClientName);
        services.AddHttpClient(DiscordBotClient.ClientName);
        services.AddSingleton<DiscordWebhookClient>();
        services.AddSingleton<DiscordBotClient>();
        services.AddSingleton<DiscordNotificationClient>();
        services.AddSingleton<INotificationClient>(sp => sp.GetRequiredService<DiscordNotificationClient>());
    }
}
