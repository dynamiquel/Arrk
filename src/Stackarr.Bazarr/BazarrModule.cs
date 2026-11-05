using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Stackarr.Bazarr.Jobs;
using Stackarr.Core;
using Stackarr.Core.Configuration;
using Stackarr.Core.Modules;
using Stackarr.Core.Services;
using Stackarr.Docker;
using Stackarr.Subtitles.Abstractions;

namespace Stackarr.Bazarr;

/// <summary>
/// Optional Bazarr subtitle provider. The shared dispatcher and UI live in
/// <c>Stackarr.Subtitles</c>; this module only contributes the container and the provider adapter.
/// </summary>
public class BazarrModule : IStackarrModule
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
            StackarrInstance stackarr = sp.GetRequiredService<StackarrInstance>();
            BazarrApiKeyStore keys = sp.GetRequiredService<BazarrApiKeyStore>();
            string url = $"http://{stackarr.GetContainerName("bazarr")}:6767";
            return new BazarrClient(url, keys.GetAsync);
        });
        services.AddSingleton<BazarrSetupService>();
        services.AddSingleton<BazarrSubtitleProvider>();
        services.AddSingleton<ISubtitleProvider>(sp => sp.GetRequiredService<BazarrSubtitleProvider>());
        services.AddHostedService<BazarrInitialiser>();
    }

    public void ConfigureJobs(IStackarrJobProvider jobs, IServiceProvider services)
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
