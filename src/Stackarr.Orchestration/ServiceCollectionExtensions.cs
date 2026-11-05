using Microsoft.Extensions.DependencyInjection;
using Stackarr.Core.Services;

namespace Stackarr.Orchestration;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddStackarrOrchestration(this IServiceCollection services, ServiceRegistry serviceRegistry)
    {
        services.AddSingleton(serviceRegistry);
        services.AddSingleton<ResourceInventory>();
        services.AddSingleton<ServiceProvisioner>();
        services.AddSingleton<StackarrProvisioner>();
        services.AddSingleton<HealthMonitor>();
        services.AddHostedService(sp => sp.GetRequiredService<HealthMonitor>());
        services.AddSingleton<ConfigSyncer>();
        services.AddHostedService(sp => sp.GetRequiredService<ConfigSyncer>());
        services.AddSingleton<StackarrOrchestrator>();
        services.AddHostedService(sp => sp.GetRequiredService<StackarrOrchestrator>());
        services.AddSingleton<IImageUpdateService, ImageUpdateService>();

        services.AddSingleton<HostMetricsService>();
        services.AddSingleton<ServiceStateManager>();
        services.AddSingleton<DashboardService>();

        return services;
    }
}
