using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Arrk.Ai;
using Arrk.Core;
using Arrk.Core.Notifications;
using Arrk.Core.Configuration;
using Arrk.Core.Documentation;
using Arrk.Core.Lifecycle;
using Arrk.Core.Modules;
using Arrk.Core.Requests;
using Arrk.Core.Services;
using Arrk.Music.Configurations;
using Arrk.Music.Jobs;
using Arrk.Music.Lyrics;
using Arrk.Music.Services;
using Arrk.Music.Tools;
using Arrk.Ui;

namespace Arrk.Music;

/// <summary>
/// Arrk's native music module. Owns the music library (artists/albums/tracks + intent), Deezer
/// discovery and identity, torrent + Soulseek acquisition, the importer, and the Music request
/// capability. Navidrome remains the player (its own module).
/// </summary>
public class MusicModule : IArrkModule
{
    public string ModuleName => "Music";
    public string ModuleDescription => "Native music library, acquisition and player UI.";

    /// <summary>No container, the music library is part of Arrk itself.</summary>
    public IReadOnlyList<Service> GetServices() => [];

    public IReadOnlyList<IConfigSectionProvider> GetConfigSections() =>
        [new ConfigSectionProvider<MusicConfig>("services:music", "Music",
            icon: "MusicNote", group: "Services", order: 20, groupOrder: 20)];

    public IReadOnlyList<ModuleDependency> GetDependencies() => [];

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDocumentation("music", typeof(MusicModule).Assembly);
        services.ConfigureConfig<MusicConfig>(configuration.GetSection("services:music"));

        // The module owns its UI contributions (all gated by services:music:enabled): navigation,
        // the discover section, the player overlay slot and its own scripts/styles.
        services.AddSingleton<INavigationProvider, MusicNavigation>();
        services.AddSingleton<IDiscoverSourceProvider, MusicDiscoverSource>();
        services.AddSingleton<IUiSlotProvider, MusicShellSlots>();
        services.AddSingleton<IHeadAssetProvider, MusicHeadAssets>();
        services.AddScoped<MusicDialogLauncher>();

        // Assistant media source: exposes the music library to the AI module's read-only tools.
        services.AddSingleton<IAssistantMediaSource, MusicAssistantMediaSource>();
        services.AddSingleton<MusicAssistantActionSource>();
        services.AddSingleton<IAssistantMediaActionSource>(sp =>
            sp.GetRequiredService<MusicAssistantActionSource>());
        services.AddSingleton<MusicAssistantRecommendationSource>();
        services.AddSingleton<IAssistantRecommendationSource>(sp =>
            sp.GetRequiredService<MusicAssistantRecommendationSource>());

        // The module owns its assistant packs and binds each to its own domain services only.
        services.AddSingleton<IAssistantTool>(sp => new MusicAssistantActionsTool(
            new AssistantActionRunner([sp.GetRequiredService<MusicAssistantActionSource>()])));
        services.AddSingleton<IAssistantTool>(sp => new MusicAssistantRecommendationsTool(
            sp.GetRequiredService<MusicAssistantRecommendationSource>()));

        services.AddSingleton<IModuleEntityConfiguration, MusicArtistConfiguration>();
        services.AddSingleton<IModuleEntityConfiguration, MusicAlbumConfiguration>();
        services.AddSingleton<IModuleEntityConfiguration, MusicTrackConfiguration>();
        services.AddSingleton<IModuleEntityConfiguration, LibraryIntentConfiguration>();
        services.AddSingleton<IModuleEntityConfiguration, GrabRecordConfiguration>();
        services.AddSingleton<IModuleEntityConfiguration, MusicAcquireQueueConfiguration>();

        services.AddSingleton(sp => new DeezerService(sp.GetRequiredService<ILogger<DeezerService>>()));
        services.AddSingleton(sp => new LrclibClient(sp.GetRequiredService<ILogger<LrclibClient>>()));

        services.AddSingleton<MusicLibrary>();
        services.AddSingleton<IMusicLibrary>(sp => sp.GetRequiredService<MusicLibrary>());
        services.AddSingleton<IMusicLibraryWriter>(sp => sp.GetRequiredService<MusicLibrary>());
        services.AddSingleton<MusicNotifications>();
        services.AddSingleton<MusicReleaseRanker>();
        services.AddSingleton<MusicAcquisitionService>();
        services.AddSingleton<MusicCatalogService>();
        services.AddSingleton<MusicLibrarySummaryService>();
        services.AddSingleton<ITileProvider, MusicTileProvider>();
        services.AddSingleton<LyricsService>();
        services.AddSingleton<MusicSubscriptionService>();
        services.AddSingleton<MusicReleaseProvider>();
        services.AddSingleton<SlskdReleaseProvider>();
        services.AddSingleton<MusicStaleEnricher>();
        services.AddSingleton<IManagedMediaProvider, MusicManagedMediaProvider>();
        services.AddSingleton<ReleaseProvider>(sp => sp.GetRequiredService<MusicReleaseProvider>());
        services.AddSingleton<ReleaseProvider>(sp => sp.GetRequiredService<SlskdReleaseProvider>());
        services.AddSingleton<IMediaRequestHandler, MusicRequestHandler>();
        services.AddSingleton<INotificationEventProvider, MusicNotificationEvents>();

        services.AddScoped<MusicDownloadImportJob>();
        services.AddScoped<MusicPlaylistSyncJob>();
        services.AddScoped<MusicReacquireJob>();
        services.AddScoped<MusicAcquireJob>();
        services.AddScoped<MusicAcquireQueueJob>();
        services.AddScoped<SoulseekFailureCheckJob>();
        services.AddScoped<MusicLyricsJob>();
        services.AddScoped<MusicLyricsResetJob>();
    }

    public void ConfigureHostedServices(IServiceCollection services)
    {
        services.AddHostedService<MusicFailureResolver>();
        services.AddHostedService<MusicSubscriptionStartupSync>();
        services.AddHostedService<MusicMonitoringReconcile>();
        services.AddHostedService<MusicLyricsStartupReset>();
    }

    /// <summary>The music module owns its art-proxy endpoints.</summary>
    public void ConfigureEndpoints(IEndpointRouteBuilder endpoints) => endpoints.MapMusicArtEndpoints();

    public void ConfigureJobs(IArrkJobProvider jobs, IServiceProvider services)
    {
        jobs
            .RegisterScheduledAndOptionsChange<MusicDownloadImportJob, MusicConfig>(
                j => j.Execute(CancellationToken.None),
                MusicDownloadImportJob.DefaultCron,
                ArrkHangfireJobProvider.MusicQueue)
            .RegisterScheduledAndOptionsChange<MusicPlaylistSyncJob, MusicConfig>(
                j => j.Execute(CancellationToken.None),
                MusicPlaylistSyncJob.DefaultCron)
            .RegisterScheduled<MusicAcquireQueueJob>(
                j => j.Execute(CancellationToken.None),
                MusicAcquireQueueJob.DefaultCron,
                ArrkHangfireJobProvider.AcquisitionQueue)
            .RegisterScheduled<SoulseekFailureCheckJob>(
                j => j.Execute(CancellationToken.None),
                SoulseekFailureCheckJob.DefaultCron,
                ArrkHangfireJobProvider.MusicQueue)
            .RegisterScheduled<MusicLyricsJob>(
                j => j.Execute(CancellationToken.None),
                MusicLyricsJob.DefaultCron,
                ArrkHangfireJobProvider.MusicQueue)
            .RegisterScheduled<MusicLyricsResetJob>(
                j => j.Execute(CancellationToken.None),
                MusicLyricsResetJob.DefaultCron);
    }
}
