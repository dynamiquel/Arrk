using Microsoft.Extensions.DependencyInjection;
using Stackarr.Core.Services;

namespace Stackarr.Speedtest;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddStackarrSpeedtest(this IServiceCollection services)
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
