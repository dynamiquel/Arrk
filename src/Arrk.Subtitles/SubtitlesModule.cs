using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Arrk.Core.Configuration;
using Arrk.Core.Modules;
using Arrk.Notifications;
using Arrk.Core.Services;
using Arrk.Subtitles.Jobs;
using Arrk.Ui;

namespace Arrk.Subtitles;

/// <summary>
/// The provider-agnostic subtitle surface and dispatcher. Backend modules contribute an
/// <see cref="ISubtitleProvider"/>; this module owns configuration, status, search, downloads,
/// notifications, scheduling and UI.
/// </summary>
public class SubtitlesModule : IArrkModule
{
    public const string EnabledConfigPath = "services:subtitles";

    public string ModuleName => "Subtitles";
    public string ModuleDescription => "Subtitle status, search and downloads.";

    public IReadOnlyList<Service> GetServices() => [];

    public IReadOnlyList<IConfigSectionProvider> GetConfigSections() =>
        [new ConfigSectionProvider<SubtitlesConfig>(EnabledConfigPath, "Subtitles",
            icon: "Subtitles", group: "Services", order: 50, groupOrder: 20)];

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.ConfigureConfig<SubtitlesConfig>(configuration.GetSection(EnabledConfigPath));

        services.AddSingleton<SubtitleLibraryResolver>();
        services.AddSingleton<SubtitleFileService>();
        services.AddSingleton<SubtitleStatusService>();
        services.AddSingleton<SubtitleService>();
        services.AddSingleton<SubtitleAutoDownloader>();
        services.AddHostedService<SubtitleImportWatcher>();
        services.AddSingleton<IHeadAssetProvider, SubtitlesHeadAssets>();
        services.AddSingleton<INotificationEventProvider, SubtitleNotificationEvents>();
        services.AddHostedService<SubtitleDownloadedNotifier>();
    }

    public void ConfigureEndpoints(IEndpointRouteBuilder endpoints) => endpoints.MapSubtitleEndpoints();

    public void ConfigureJobs(IArrkJobProvider jobs, IServiceProvider services)
    {
        jobs.RegisterScheduledAndOptionsChange<SubtitleCatchUpJob, SubtitlesConfig>(
            job => job.Execute(CancellationToken.None),
            SubtitleCatchUpJob.DefaultCron);
    }
}
