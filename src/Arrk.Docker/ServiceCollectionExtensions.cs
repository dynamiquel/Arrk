using Microsoft.Extensions.DependencyInjection;

namespace Arrk.Docker;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddArrkDocker(this IServiceCollection services)
    {
        services.AddSingleton(_ => DockerClientFactory.Create());
        services.AddSingleton<ContainerManager>();
        services.AddSingleton<NetworkManager>();
        services.AddSingleton<ImageManager>();
        services.AddSingleton<DockerMetricsCollector>();
        services.AddSingleton<DockerEventMonitor>();
        services.AddHostedService(sp => sp.GetRequiredService<DockerEventMonitor>());

        return services;
    }
}
