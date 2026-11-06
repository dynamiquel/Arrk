using Microsoft.Extensions.DependencyInjection;
using Arrk.Core.Services;

namespace Arrk.Orchestration;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddArrkOrchestration(this IServiceCollection services, ServiceRegistry serviceRegistry)
    {
        services.AddSingleton(serviceRegistry);
        services.AddSingleton<ResourceInventory>();
        services.AddSingleton<ServiceProvisioner>();
        services.AddSingleton<ArrkProvisioner>();
        services.AddSingleton<HealthMonitor>();
        services.AddHostedService(sp => sp.GetRequiredService<HealthMonitor>());
        services.AddSingleton<ConfigSyncer>();
        services.AddHostedService(sp => sp.GetRequiredService<ConfigSyncer>());
        services.AddSingleton<ArrkOrchestrator>();
        services.AddHostedService(sp => sp.GetRequiredService<ArrkOrchestrator>());
        services.AddSingleton<IImageUpdateService, ImageUpdateService>();

        services.AddSingleton<HostMetricsService>();
        services.AddSingleton<ServiceStateManager>();
        services.AddSingleton<DashboardService>();

        return services;
    }
}
