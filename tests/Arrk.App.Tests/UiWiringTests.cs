using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Arrk.App;
using Arrk.App.Services.Ui;
using Arrk.Configuration;
using Arrk.Core;
using Arrk.Core.Configuration;
using Arrk.Core.Services;
using Arrk.Ui;
namespace Arrk.App.Tests;

/// <summary>
/// Exercises the real UI composition: building the registry over every built-in module proves
/// there are no duplicate routes across modules, that each module (not the shell) contributes its
/// own navigation, and that switching a module's service off hides its entries live.
/// </summary>
public class UiWiringTests
{
    static IConfiguration Config(params (string Key, string Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => (string?)s.Value))
            .Build();

    static ServiceProvider BuildProvider(IConfiguration configuration)
    {
        ModuleLoader loader = ServiceRegistryFactory.CreateModuleLoader();
        ServiceCollection services = new();
        loader.ConfigureAllServices(services, configuration);
        services.AddSingleton<IUiAssemblyProvider>(new UiAssemblyProvider(loader));
        services.AddSingleton<INavigationProvider, AppNavigationProvider>();
        services.AddSingleton<IHeadAssetProvider, AppHeadAssetProvider>();
        services.AddSingleton(configuration);
        services.AddSingleton<UiRegistry>();
        services.AddArrkTiles();

        // Deliberately not disposed: the registries subscribe to config reloads and the test wants
        // them alive for the assertions.
        return services.BuildServiceProvider();
    }

    static UiRegistry BuildRegistry(IConfiguration configuration) =>
        BuildProvider(configuration).GetRequiredService<UiRegistry>();

    static TileRegistry BuildTiles(IConfiguration configuration) =>
        BuildProvider(configuration).GetRequiredService<TileRegistry>();

    sealed class UnusedDbContextFactory : IDbContextFactory<ArrkDbContext>
    {
        public ArrkDbContext CreateDbContext() =>
            throw new InvalidOperationException("The default layout test does not use preferences.");

        public Task<ArrkDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The default layout test does not use preferences.");
    }

    [Test]
    public async Task Modules_ContributeTheAiAndAuthConfigSections()
    {
        ModuleLoader loader = ServiceRegistryFactory.CreateModuleLoader();
        ConfigSectionRegistry registry = new();
        ConfigDefaults.RegisterCoreSections(registry);
        loader.RegisterConfigSections(registry);

        List<string> paths = registry.GetAll().Select(section => section.SectionPath).ToList();

        await Assert.That(paths).Contains("ai");
        await Assert.That(paths).Contains("auth");
    }

    [Test]
    public async Task Modules_ContributeTheirOwnNavigation()
    {
        UiRegistry registry = BuildRegistry(Config());

        string[] hrefs = [.. registry.Navigation.Select(e => e.Href)];

        await Assert.That(hrefs).Contains("/discover"); // shell
        await Assert.That(hrefs).Contains("/movies");   // Tvarr
        await Assert.That(hrefs).Contains("/tv");       // Tvarr
        await Assert.That(hrefs).Contains("/music");    // Music
        await Assert.That(hrefs).Contains("/events");   // Notifications
        await Assert.That(hrefs).Contains("/requests"); // Requests
        await Assert.That(hrefs).Contains("/downloads"); // DownloadManagement
        await Assert.That(hrefs).Contains("/docker");    // Orchestration
        await Assert.That(hrefs).Contains("/scheduler"); // Orchestration
        await Assert.That(hrefs).Contains("/config");    // Configuration
        await Assert.That(hrefs).Contains("/ai");        // AI usage
    }

    [Test]
    public async Task DisablingRequests_HidesItsNavigationLive()
    {
        UiRegistry registry = BuildRegistry(Config(("requests:enabled", "false")));

        string[] hrefs = [.. registry.Navigation.Select(e => e.Href)];

        await Assert.That(hrefs).DoesNotContain("/requests");
        await Assert.That(hrefs).Contains("/events");
    }

    [Test]
    public async Task DisablingTvService_HidesItsNavigationLive()
    {
        UiRegistry registry = BuildRegistry(Config(("services:tv:enabled", "false")));

        string[] hrefs = [.. registry.Navigation.Select(e => e.Href)];

        await Assert.That(hrefs).DoesNotContain("/movies");
        await Assert.That(hrefs).DoesNotContain("/tv");
        await Assert.That(hrefs).Contains("/music");
    }

    [Test]
    public async Task MalModule_ContributesNavigationAndRegions()
    {
        UiRegistry registry = BuildRegistry(Config());

        await Assert.That(registry.Navigation.Select(e => e.Href)).Contains("/mal");
        // MyAnimeList and Discord direct messages both contribute to account connections.
        await Assert.That(registry.SlotsFor(UiRegions.AccountConnections).Count).IsEqualTo(2);
        // MyAnimeList and the subtitle season action both contribute to this region.
        await Assert.That(registry.SlotsFor("tv.series.season").Count).IsEqualTo(2);
    }

    [Test]
    public async Task DisablingAi_HidesTheAssistantLauncher()
    {
        UiRegistry registry = BuildRegistry(Config(("ai:enabled", "false")));

        await Assert.That(registry.SlotsFor(UiRegions.ShellAppBarActions).Count).IsEqualTo(2);
    }

    [Test]
    public async Task ShellRegions_AreContributedByModules()
    {
        // The app-bar bell (Notifications) and assistant launcher (AI) plus the player overlay
        // (Music) are all module slots now; the shell itself contributes no slots of its own.
        UiRegistry registry = BuildRegistry(Config());

        await Assert.That(registry.SlotsFor(UiRegions.ShellAppBarActions).Count).IsEqualTo(3);
        await Assert.That(registry.SlotsFor(UiRegions.ShellOverlays).Count).IsEqualTo(1);
    }

    [Test]
    public async Task RegionContributions_AcceptEveryContextTheirRegionsHandOut()
    {
        // The item links regions are shared by library detail dialogs and the remote media popup.
        // A contribution with a narrower Context parameter would throw during parameter binding and
        // surface as an error-boundary warning instead of simply staying hidden.
        UiRegistry registry = BuildRegistry(Config());
        Dictionary<string, Type[]> contexts = new()
        {
            ["tv.movie.links"] = [typeof(TvMovieActionContext), typeof(TvMediaLinkContext)],
            ["tv.series.links"] = [typeof(TvSeriesActionContext), typeof(TvMediaLinkContext)]
        };

        HashSet<string> checkedComponents = [];
        foreach ((string region, Type[] contextTypes) in contexts)
        {
            foreach (UiSlot slot in registry.SlotsFor(region))
            {
                PropertyInfo? property = slot.Component.GetProperty("Context");
                if (property is null)
                    continue;

                checkedComponents.Add(slot.Component.Name);
                foreach (Type contextType in contextTypes)
                {
                    await Assert.That(property.PropertyType.IsAssignableFrom(contextType))
                        .IsTrue()
                        .Because($"{slot.Component.Name} in {region} cannot receive {contextType.Name}");
                }
            }
        }

        await Assert.That(checkedComponents).Contains("MovieSubtitleActions");
        await Assert.That(checkedComponents).Contains("SeriesSubtitleActions");
    }

    [Test]
    public async Task Modules_ContributeDashboardTiles()
    {
        TileRegistry tiles = BuildTiles(Config());
        string[] ids = [.. tiles.All.Select(t => t.Id)];

        await Assert.That(ids).Contains("services.status");
        await Assert.That(ids).Contains("system.host");
        await Assert.That(ids).Contains("torrents.download");
        await Assert.That(ids).Contains("system.storage");
        await Assert.That(ids).Contains("system.speedtest");
        await Assert.That(ids).Contains("movies.library");
        await Assert.That(ids).Contains("shows.shows");
        await Assert.That(ids).Contains("music.albums");
        await Assert.That(ids).Contains("requests.open");
        await Assert.That(ids).Contains("history.plays");
        await Assert.That(ids).Contains("lifecycle.stale");
        await Assert.That(ids).Contains("ai.calls");
        await Assert.That(ids).Contains("tv.continue-watching");
        await Assert.That(ids).Contains("tv.watchlists");
        await Assert.That(ids).Contains("music.recently-played");
        await Assert.That(ids).Contains("music.playlists");
        await Assert.That(ids).Contains("requests.pending");
    }

    [Test]
    public async Task DefaultDashboard_IsTheHostAndLibraryLayout()
    {
        TileRegistry tiles = BuildTiles(Config());
        DashboardLayoutService layouts = new(
            new UserUiPreferenceService(new UnusedDbContextFactory()), tiles, Config());

        DashboardLayout layout = layouts.CreateDefault(isAdmin: true);

        await Assert.That(layout.Sections.Select(s => s.Title)).IsEquivalentTo(["Host", "Library"]);
        await Assert.That(layout.Sections[0].Tiles.Select(t => t.Id)).IsEquivalentTo(
            ["services.status", "system.updated", "services.unhealthy", "system.host", "system.storage", "system.speedtest"]);
        await Assert.That(layout.Sections[0].Tiles.Select(t => t.Size ?? TileSize.Medium)).IsEquivalentTo(
            [TileSize.VeryLarge, TileSize.Small, TileSize.Small, TileSize.Small, TileSize.Medium, TileSize.Medium]);
        await Assert.That(layout.Sections[1].Tiles.Select(t => t.Id)).IsEquivalentTo(
            ["requests.pending", "tv.continue-watching", "tv.watchlists", "music.recently-played", "music.playlists"]);
        await Assert.That(layout.Sections[1].Tiles.Select(t => t.Size ?? TileSize.Medium)).IsEquivalentTo(
            [TileSize.VeryLarge, TileSize.VeryLarge, TileSize.VeryLarge, TileSize.Large, TileSize.Large]);
    }

    [Test]
    public async Task DisablingTv_RemovesItsTilesLive()
    {
        TileRegistry tiles = BuildTiles(Config(("services:tv:enabled", "false")));
        string[] ids = [.. tiles.All.Select(t => t.Id)];

        await Assert.That(ids).DoesNotContain("movies.library");
        await Assert.That(ids).DoesNotContain("shows.shows");
        await Assert.That(ids).Contains("music.albums");
    }

    [Test]
    public async Task DockerAndServiceTiles_LinkToTheDockerPageForAdmins()
    {
        TileRegistry tiles = BuildTiles(Config());

        TileDefinition docker = tiles.Find("docker.load")!;
        TileDefinition services = tiles.Find("services.running")!;

        await Assert.That(docker.Href).IsEqualTo("/docker");
        await Assert.That(docker.AdminOnly).IsTrue();
        await Assert.That(services.Href).IsEqualTo("/docker");
        await Assert.That(services.LinkAdminOnly).IsTrue();
    }

    [Test]
    public async Task TorrentTile_KeepsItsContextOnTheDashboard()
    {
        TileRegistry tiles = BuildTiles(Config());

        TileDefinition download = tiles.Find("torrents.download")!;

        await Assert.That(download.TitleFor(TilePlacement.Dashboard)).IsEqualTo("TORRENTS - DOWNLOAD");
        await Assert.That(download.TitleFor(TilePlacement.Page)).IsEqualTo("Download");
    }

}
