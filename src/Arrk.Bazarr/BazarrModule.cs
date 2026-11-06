using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Arrk.Bazarr.Jobs;
using Arrk.Core;
using Arrk.Core.Configuration;
using Arrk.Core.Modules;
using Arrk.Core.Services;
using Arrk.Docker;
using Arrk.Subtitles.Abstractions;

namespace Arrk.Bazarr;

/// <summary>
/// Optional Bazarr subtitle provider. The shared dispatcher and UI live in
/// <c>Arrk.Subtitles</c>; this module only contributes the container and the provider adapter.
/// </summary>
public class BazarrModule : IArrkModule
{
    public const string EnabledConfigPath = "services:subtitles:bazarr";

    public string ModuleName => "Subtitles (Bazarr)";
    public string ModuleDescription => "Optional Bazarr subtitle provider.";

    public IReadOnlyList<Service> GetServices()
    {
        return [new(new BazarrServiceDefinition())];
    }

    public IReadOnlyList<IConfigSectionProvider> GetConfigSections() =>
        [new ConfigSectionProvider<BazarrConfig>(EnabledConfigPath, "Bazarr",
            icon: "Subtitles", group: "Services", order: 52, groupOrder: 20)];

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.ConfigureConfig<BazarrConfig>(configuration.GetSection(EnabledConfigPath));
        services.ConfigureConfig<SubtitlesConfig>(configuration.GetSection("services:subtitles"));

        services.AddSingleton<BazarrApiKeyStore>();
        services.AddSingleton(sp =>
        {
            ArrkInstance arrk = sp.GetRequiredService<ArrkInstance>();
            BazarrApiKeyStore keys = sp.GetRequiredService<BazarrApiKeyStore>();
            string url = $"http://{arrk.GetContainerName("bazarr")}:6767";
            return new BazarrClient(url, keys.GetAsync);
        });
        services.AddSingleton<BazarrSetupService>();
        services.AddSingleton<BazarrSubtitleProvider>();
        services.AddSingleton<ISubtitleProvider>(sp => sp.GetRequiredService<BazarrSubtitleProvider>());
        services.AddHostedService<BazarrInitialiser>();
    }

    public void ConfigureJobs(IArrkJobProvider jobs, IServiceProvider services)
    {
        jobs.RegisterScheduledAndOptionsChange<BazarrConfigSyncJob, BazarrConfig>(
            job => job.Execute(CancellationToken.None),
            BazarrConfigSyncJob.DefaultCron);
    }

    public IReadOnlyList<ModuleDependency> GetDependencies() =>
    [
        new("Bazarr", "GPL-3.0", "morpheus65535", "https://github.com/morpheus65535/bazarr", "Companion application to Sonarr and Radarr for managing and downloading subtitles"),
    ];
}
