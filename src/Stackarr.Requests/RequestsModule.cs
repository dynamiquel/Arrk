using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Stackarr.Core;
using Stackarr.Core.Configuration;
using Stackarr.Ai;
using Stackarr.Core.Modules;
using Stackarr.Core.Notifications;
using Stackarr.Core.Requests;
using Stackarr.Core.Services;
using Stackarr.Requests.Configurations;
using Stackarr.Requests.Tools;
using Stackarr.Ui;

namespace Stackarr.Requests;

/// <summary>
/// The user request system (a Seerr-style request/approval flow). It owns no container; it
/// contributes configuration, entities, the request service, notification topics and the registry
/// that aggregates the media-request capabilities provided by library-manager modules. It depends
/// only on <c>Stackarr.Core</c>, never on a concrete backend.
/// </summary>
public class RequestsModule : IStackarrModule
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
