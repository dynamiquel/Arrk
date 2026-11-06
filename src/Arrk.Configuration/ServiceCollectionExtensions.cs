using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Arrk.Core;
using Arrk.Core.Configuration;

namespace Arrk.Configuration;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddArrkConfiguration(this IServiceCollection services)
    {
        services.AddSingleton<IValidator<ArrkConfig>, ArrkConfigValidator>();
        services.AddSingleton<IValidator<StorageConfig>, StorageConfigValidator>();
        services.AddSingleton<IValidator<VpnConfig>, VpnConfigValidator>();
        services.AddSingleton<IValidator<TorrentDownloaderConfig>, TorrentDownloaderConfigValidator>();

        return services;
    }
}
