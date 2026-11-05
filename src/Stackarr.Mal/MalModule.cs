using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Stackarr.Core;
using Stackarr.Auth.Abstractions;
using Stackarr.Core.Configuration;
using Stackarr.Ai;
using Stackarr.Core.Modules;
using Stackarr.Core.Playback;
using Stackarr.Core.Services;
using Microsoft.AspNetCore.Routing;
using Stackarr.Mal.Auth;
using Stackarr.Mal.Clients;
using Stackarr.Mal.Configurations;
using Stackarr.Mal.Jobs;
using Stackarr.Mal.Services;
using Stackarr.Mal.Tools;
using Stackarr.Tvarr.Services;
using Stackarr.Ui;

namespace Stackarr.Mal;

/// <summary>
/// Native MyAnimeList integration: OAuth linking/sign-in, list browsing and editing, one-way watch
/// progress sync, and the bidirectional Plan to Watch watchlist contribution.
/// </summary>
public class MalModule : IStackarrModule
{
    public string ModuleName => "MyAnimeList";
    public string ModuleDescription => "MyAnimeList account linking, list management and watch-progress sync.";

    public IReadOnlyList<Service> GetServices() => [];

    public IReadOnlyList<IConfigSectionProvider> GetConfigSections() =>
        [new ConfigSectionProvider<MalConfig>(
            MalConfig.SectionPath,
            "MyAnimeList",
            icon: "PlaylistPlay",
            group: "Services",
            order: 85,
            groupOrder: 20)];

    public IReadOnlyList<ModuleDependency> GetDependencies() =>
    [
        new("MyAnimeList API", "MAL API terms", "MyAnimeList", "https://myanimelist.net/apiconfig",
            "Official MyAnimeList API used for account OAuth, list data and progress updates"),
        new("anime-lists", "ODbL", "Fribb", "https://github.com/Fribb/anime-lists",
            "Community mapping dataset between MAL, TVDB and TMDB ids")
    ];

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<MalConfig>(configuration.GetSection(MalConfig.SectionPath));

        services.AddSingleton<INavigationProvider, MalNavigation>();
        services.AddSingleton<IHeadAssetProvider, MalHeadAssets>();
        services.AddSingleton<IExternalLoginProvider, MalLoginProvider>();
        services.AddSingleton<IExternalWatchlist>(sp => new MalWatchlistProvider(
            sp.GetRequiredService<IDbContextFactory<StackarrDbContext>>(),
            sp.GetRequiredService<MalAccountService>(),
            sp.GetRequiredService<MalListService>(),
            sp.GetRequiredService<MalMappingService>(),
            sp.GetRequiredService<MalMappingDataset>(),
            sp.GetRequiredService<ITvCatalog>(),
            MalStatuses.PlanToWatch,
            "MAL Plan to Watch",
            sp.GetRequiredService<ILogger<MalWatchlistProvider>>()));
        services.AddSingleton<IExternalWatchlist>(sp => new MalWatchlistProvider(
            sp.GetRequiredService<IDbContextFactory<StackarrDbContext>>(),
            sp.GetRequiredService<MalAccountService>(),
            sp.GetRequiredService<MalListService>(),
            sp.GetRequiredService<MalMappingService>(),
            sp.GetRequiredService<MalMappingDataset>(),
            sp.GetRequiredService<ITvCatalog>(),
            MalStatuses.Watching,
            "MAL Watching",
            sp.GetRequiredService<ILogger<MalWatchlistProvider>>()));

        services.AddSingleton<IModuleEntityConfiguration, MalAccountConfiguration>();
        services.AddSingleton<IModuleEntityConfiguration, MalListEntryConfiguration>();
        services.AddSingleton<IModuleEntityConfiguration, MalCatalogConfiguration>();
        services.AddSingleton<IModuleEntityConfiguration, MalMapConfiguration>();
        services.AddSingleton<IModuleEntityConfiguration, MalMapPartConfiguration>();

        services.AddSingleton<MalTokenProtector>();
        services.AddSingleton<MalClient>();
        services.AddSingleton<MalMappingDataset>();
        services.AddSingleton<MalAccountService>();
        services.AddSingleton<MalOAuthService>();
        services.AddSingleton<MalCatalogService>();
        services.AddSingleton<MalMappingService>();
        services.AddSingleton<MalListService>();
        services.AddSingleton<IAssistantTool, MalAssistantTool>();
        services.AddSingleton<MalSyncService>();


        services.AddScoped<MalWatchSyncJob>();
        services.AddScoped<MalDatasetRefreshJob>();
    }

    public void ConfigureHostedServices(IServiceCollection services)
    {
        services.AddHostedService<MalDatasetWarmup>();
    }

    public void ConfigureJobs(IStackarrJobProvider jobs, IServiceProvider services)
    {
        Microsoft.Extensions.Options.IOptionsMonitor<MalConfig> config =
            services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<MalConfig>>();

        jobs.RegisterScheduled<MalWatchSyncJob>(
                j => j.Execute(CancellationToken.None),
                CronFor(config.CurrentValue.SyncMinutes))
            .RegisterScheduled<MalDatasetRefreshJob>(
                j => j.Execute(CancellationToken.None),
                MalDatasetRefreshJob.DefaultCron);

        config.OnChange((current, _) =>
        {
            try
            {
                jobs.UpdateSchedule<MalWatchSyncJob>(CronFor(current.SyncMinutes));
            }
            catch
            {
                /* keep the previous valid schedule */
            }
        });
    }

    static string CronFor(int minutes) => minutes switch
    {
        <= 1 => "* * * * *",
        < 60 => $"*/{minutes} * * * *",
        _ => $"0 */{Math.Clamp(minutes / 60, 1, 23)} * * *"
    };

    public void ConfigureEndpoints(IEndpointRouteBuilder endpoints) => endpoints.MapMalEndpoints();
}

public sealed class MalNavigation : INavigationProvider
{
    public const string EnabledConfigPath = MalConfig.SectionPath;

    public IReadOnlyList<NavigationEntry> Entries { get; } =
    [
        new(UiSections.Browse, "MyAnimeList", "/mal", MudBlazor.Icons.Material.Filled.PlaylistPlay,
            Order: 22, EnabledConfigPath: EnabledConfigPath,
            LogoUrl: "/_content/Stackarr.Mal/img/mal-logo.svg")
    ];
}

public sealed class MalHeadAssets : IHeadAssetProvider
{
    public IReadOnlyList<HeadAsset> Assets { get; } =
    [
        new("_content/Stackarr.Mal/css/mal.css", HeadAssetKind.Style, Order: 134)
    ];
}
