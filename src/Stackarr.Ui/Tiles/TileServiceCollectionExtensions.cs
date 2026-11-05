using Microsoft.Extensions.DependencyInjection;

namespace Stackarr.Ui;

/// <summary>Registers the shared tile catalogue and per-account UI customisation services.</summary>
public static class TileServiceCollectionExtensions
{
    public static IServiceCollection AddStackarrTiles(this IServiceCollection services)
    {
        services.AddSingleton<TileRegistry>();
        services.AddSingleton<DashboardLayoutService>();
        services.AddSingleton<UserCssService>();
        return services;
    }
}
