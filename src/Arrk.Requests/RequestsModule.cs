using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Arrk.Core;
using Arrk.Core.Configuration;
using Arrk.Ai;
using Arrk.Core.Modules;
using Arrk.Notifications;
using Arrk.Core.Requests;
using Arrk.Core.Services;
using Arrk.Requests.Configurations;
using Arrk.Requests.Tools;
using Arrk.Ui;

namespace Arrk.Requests;

/// <summary>
/// The user request system (a Seerr-style request/approval flow). It owns no container; it
/// contributes configuration, entities, the request service, notification topics and the registry
/// that aggregates the media-request capabilities provided by library-manager modules. It depends
/// only on <c>Arrk.Core</c>, never on a concrete backend.
/// </summary>
public class RequestsModule : IArrkModule
{
    public string ModuleName => "Requests";
    public string ModuleDescription => "User request system (Seerr replacement).";

    // No container: requests are an in-app feature fulfilled through other modules' handlers.
    public IReadOnlyList<Service> GetServices() => [];

    public IReadOnlyList<IConfigSectionProvider> GetConfigSections() =>
        [new ConfigSectionProvider<RequestsConfig>("requests", "Requests",
            icon: "Inbox", group: "General", order: 5, groupOrder: 10)];

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<RequestsConfig>(configuration.GetSection("requests"));

        services.AddSingleton<IModuleEntityConfiguration, MediaRequestConfiguration>();
        services.AddSingleton<IModuleEntityConfiguration, RequestBlacklistConfiguration>();
        services.AddSingleton<INotificationEventProvider, RequestsNotificationEvents>();
        services.AddSingleton<INavigationProvider, RequestsNavigation>();
        services.AddSingleton<ITileProvider, RequestsTileProvider>();
        services.AddSingleton<IMediaRequestRegistry>(sp =>
            new MediaRequestRegistry(sp.GetServices<IMediaRequestHandler>()));
        services.AddScoped<RequestService>();
        services.AddSingleton<IAssistantTool, RequestsAssistantTool>();
    }

    public void ConfigureHostedServices(IServiceCollection services)
    {
        services.AddHostedService<RequestMediaRemovalListener>();
        services.AddHostedService<RequestAvailabilityListener>();
    }
}
