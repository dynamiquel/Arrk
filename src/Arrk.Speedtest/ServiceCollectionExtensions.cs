using Microsoft.Extensions.DependencyInjection;
using Arrk.Core.Services;

namespace Arrk.Speedtest;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddArrkSpeedtest(this IServiceCollection services)
    {
        services.AddHttpClient<CloudflareSpeedtestClient>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(120);
        });
        services.AddSingleton<VpnSpeedtestClient>();
        services.AddSingleton<IConnectionHealthProvider, SpeedtestConnectionHealthProvider>();
        return services;
    }
}
