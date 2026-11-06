using Microsoft.Extensions.DependencyInjection;

namespace Arrk.Ui;

/// <summary>Registers the shared tile catalogue and per-account UI customisation services.</summary>
public static class TileServiceCollectionExtensions
{
    public static IServiceCollection AddArrkTiles(this IServiceCollection services)
    {
        services.AddSingleton<TileRegistry>();
        services.AddSingleton<DashboardLayoutService>();
        services.AddSingleton<UserCssService>();
        return services;
    }
}
