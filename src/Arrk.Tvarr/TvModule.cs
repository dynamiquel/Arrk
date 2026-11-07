using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Hangfire;
using Arrk.Ai;
using Arrk.Core;
using Arrk.Core.Configuration;
using Arrk.Core.Documentation;
using Arrk.Core.Lifecycle;
using Arrk.Core.Models;
using Arrk.Core.Modules;
using Arrk.Core.Notifications;
using Arrk.Core.Requests;
using Arrk.Core.Services;
using Arrk.Tvarr.Clients;
using Arrk.Tvarr.Configurations;
using Arrk.Tvarr.Jobs;
using Arrk.Tvarr.Models;
using Arrk.Tvarr.Tools;
using Arrk.Tvarr.Services;
using Arrk.Ui;

namespace Arrk.Tvarr;

public class TvModule : IArrkModule
{
    public const string ServiceApiKey = "stackarr";

    public string ModuleName => "TV";
    public string ModuleDescription => "Movies and TV through Radarr and Sonarr.";

    public IReadOnlyList<Service> GetServices()
    {
        return [new(new TvServiceDefinition())];
    }

    public IReadOnlyList<IConfigSectionProvider> GetConfigSections() =>
        [new ConfigSectionProvider<TvConfig>("services:tv", "TV",
            icon: "Tv", group: "Services", order: 10, groupOrder: 20)];

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDocumentation("tvarr", typeof(TvModule).Assembly);
        services.Configure<TvConfig>(configuration.GetSection("services:tv"));

        // The module owns its navigation (gated by services:tv:enabled) and its discovery section,
        // so the shell stays unaware of which media modules exist.
        services.AddSingleton<INavigationProvider, TvNavigation>();
        services.AddSingleton<IDiscoverSourceProvider, TvDiscoverSource>();
        services.AddSingleton<IUiSlotProvider, TvShellSlots>();
        services.AddSingleton<IHeadAssetProvider, TvHeadAssets>();

        // TMDB metadata is the module's preferred search/metadata layer; the credentials resolve per
        // call so a config edit applies without a restart. Radarr/Sonarr are the fallback.
        services.AddSingleton(sp =>
        {
            IOptionsMonitor<MetadataConfig> metadataMonitor = sp.GetRequiredService<IOptionsMonitor<MetadataConfig>>();
            IConfiguration config = sp.GetRequiredService<IConfiguration>();
            return new TmdbService(
                () =>
                {
                    MetadataConfig current = metadataMonitor.CurrentValue;
                    string apiKey = !string.IsNullOrWhiteSpace(current.TmdbApiKey) ? current.TmdbApiKey : config["tmdbApiKey"] ?? "";
                    string readToken = !string.IsNullOrWhiteSpace(current.TmdbReadToken) ? current.TmdbReadToken : config["tmdbReadToken"] ?? "";
                    return (apiKey, readToken);
                },
                sp.GetRequiredService<ILogger<TmdbService>>());
        });
        services.AddSingleton<MediaCatalogService>();
        services.AddSingleton<WatchStateEnricher>();
        services.AddSingleton<ContinueWatchingService>();
        services.AddSingleton<StaleMediaEnricher>();
        services.AddSingleton<TvLibrarySummaryService>();
        services.AddSingleton<ITileProvider, TvTileProvider>();
        services.AddSingleton<IManagedMediaProvider, TvManagedMediaProvider>();

        services.AddSingleton<RadarrClient>(sp =>
        {
            ArrkInstance arrk = sp.GetRequiredService<ArrkInstance>();
            string url = $"http://{arrk.GetContainerName("radarr")}:7878/radarr";
            return new(url, ServiceApiKey);
        });

        services.AddSingleton<SonarrClient>(sp =>
        {
            ArrkInstance arrk = sp.GetRequiredService<ArrkInstance>();
            string url = $"http://{arrk.GetContainerName("sonarr")}:8989/sonarr";
            return new(url, ServiceApiKey);
        });

        services.AddSingleton<RadarrService>();
        services.AddSingleton<SonarrService>();

        // Cross-module contracts: other modules depend on these roles, never on the arr services.
        services.AddSingleton<IMovieLibrary>(sp => sp.GetRequiredService<RadarrService>());
        services.AddSingleton<IMovieLibraryWriter>(sp => sp.GetRequiredService<RadarrService>());
        services.AddSingleton<ISeriesLibrary>(sp => sp.GetRequiredService<SonarrService>());
        services.AddSingleton<ISeriesLibraryWriter>(sp => sp.GetRequiredService<SonarrService>());
        services.AddSingleton<ITvCatalog>(sp => sp.GetRequiredService<MediaCatalogService>());

        // Interactive-search release sources (shared picker in the UI).
        services.AddSingleton<RadarrReleaseProvider>();
        services.AddSingleton<SonarrReleaseProvider>();

        // Assistant media source: exposes the TV/movie library to the AI module's read-only tools.
        services.AddSingleton<IAssistantMediaSource, TvarrAssistantMediaSource>();
        services.AddSingleton<TvarrAssistantActionSource>();
        services.AddSingleton<IAssistantMediaActionSource>(sp =>
            sp.GetRequiredService<TvarrAssistantActionSource>());
        services.AddSingleton<TvarrAssistantRecommendationSource>();
        services.AddSingleton<IAssistantRecommendationSource>(sp =>
            sp.GetRequiredService<TvarrAssistantRecommendationSource>());

        // The module owns its assistant packs and binds each to its own domain services only.
        services.AddSingleton<IAssistantTool>(sp => new TvarrAssistantActionsTool(
            new AssistantActionRunner([sp.GetRequiredService<TvarrAssistantActionSource>()])));
        services.AddSingleton<IAssistantTool>(sp => new TvarrAssistantRecommendationsTool(
            sp.GetRequiredService<TvarrAssistantRecommendationSource>()));

        services.AddSingleton<IMediaProbe>(sp =>
            new FfprobeMediaProbe(sp.GetService<ILogger<FfprobeMediaProbe>>()));
        services.AddSingleton<IMediaPathMapper>(sp =>
        {
            IOptionsMonitor<ArrkConfig> config = sp.GetRequiredService<IOptionsMonitor<ArrkConfig>>();
            // Resolve the storage roots per call so a storage config edit applies without a restart.
            return new ArrMediaPathMapper(() =>
            {
                VolumePathResolver resolver = new(config.CurrentValue.Storage);
                return
                [
                    ("/movies", resolver.ResolveToLocalPath(RelativeVolumes.TvMovies)),
                    ("/shows", resolver.ResolveToLocalPath(RelativeVolumes.TvShows))
                ];
            });
        });
        services.AddSingleton<MediaInfoService>();
        services.AddSingleton<TvUploadStore>();
        services.AddSingleton<TvImportAiMatcher>();
        services.AddSingleton<TvImportService>();
        services.AddSingleton<TvUploadCleanupJob>();
        services.AddHostedService<DownloadEventHandler>();
        services.AddSingleton<MediaImportMonitor>();
        services.AddSingleton<TorrentCleanupJob>();
        services.AddSingleton<FailedDownloadHandlingSync>();
        services.AddSingleton<IModuleEntityConfiguration, PendingTorrentCleanupConfiguration>();
        services.AddSingleton<IModuleEntityConfiguration, TvUploadRecordConfiguration>();
        services.AddSingleton<INotificationEventProvider, MediaNotificationEvents>();
        services.AddSingleton<IMediaRequestHandler, TvarrRequestHandler>();
        services.AddSingleton<IMediaDownloadEntityResolver, TvDownloadEntityResolver>();
    }

    public IReadOnlyList<ModuleDependency> GetDependencies() =>
    [
        new("Radarr", "GPL-3.0", "Radarr", "https://github.com/Radarr/Radarr", "Movie management Usenet and BitTorrent download automation"),
        new("Sonarr", "GPL-3.0", "Sonarr", "https://github.com/Sonarr/Sonarr", "TV show management Usenet and BitTorrent download automation"),
        new("FFmpeg (ffprobe)", "LGPL-2.1", "FFmpeg Team", "https://ffmpeg.org/", "Media probing used to extract detailed stream information for the media info view"),
    ];

    public void ConfigureEndpoints(IEndpointRouteBuilder endpoints) => endpoints.MapTvUploadEndpoints();

    public void ConfigureJobs(IArrkJobProvider jobs, IServiceProvider services)
    {
        jobs
            .RegisterScheduled<TvUploadCleanupJob>(
                job => job.Execute(CancellationToken.None),
                TvUploadCleanupJob.DefaultCron)
            .RegisterScheduledAndOptionsChange<RootFolderSyncJob, TvConfig>(
                j => j.Execute(CancellationToken.None),
                RootFolderSyncJob.DefaultCron)
            .RegisterScheduledAndOptionsChange<QualityProfileSyncJob, TvConfig>(
                j => j.Execute(CancellationToken.None),
                QualityProfileSyncJob.DefaultCron)
            .RegisterScheduledAndOptionsChange<DownloadClientSyncJob, TvConfig>(
                j => j.Execute(CancellationToken.None),
                DownloadClientSyncJob.DefaultCron)
            .RegisterScheduledAndOptionsChange<ProwlarrAppSyncJob, TvConfig>(
                j => j.Execute(CancellationToken.None),
                ProwlarrAppSyncJob.DefaultCron)
            .RegisterScheduledAndOptionsChange<FailedDownloadHandlingSyncJob, TvConfig>(
                j => j.Execute(CancellationToken.None),
                FailedDownloadHandlingSyncJob.DefaultCron)
            .RegisterScheduled<MissingContentSearchJob>(
                j => j.Execute(CancellationToken.None),
                MissingContentSearchJob.DefaultCron);

        RecurringJob.AddOrUpdate<TorrentCleanupJob>(
            "torrent-cleanup",
            s => s.Execute(CancellationToken.None),
            "* * * * *");

        // Announce movies/episodes the arrs import into the library - runs every minute
        RecurringJob.AddOrUpdate<MediaImportMonitorJob>(
            "media-import-monitor",
            j => j.Execute(CancellationToken.None),
            MediaImportMonitorJob.DefaultCron);
    }
}
