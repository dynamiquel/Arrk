using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Stackarr.Core;
using Stackarr.Core.Configuration;

namespace Stackarr.Configuration;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddStackarrConfiguration(this IServiceCollection services)
    {
        services.AddSingleton<IValidator<StackarrConfig>, StackarrConfigValidator>();
        services.AddSingleton<IValidator<StorageConfig>, StorageConfigValidator>();
        services.AddSingleton<IValidator<VpnConfig>, VpnConfigValidator>();
        services.AddSingleton<IValidator<TorrentDownloaderConfig>, TorrentDownloaderConfigValidator>();

        return services;
    }
}
