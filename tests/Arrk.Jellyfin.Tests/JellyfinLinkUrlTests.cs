using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Arrk.Core.Configuration;
using Arrk.Core.Models;
using Arrk.Jellyfin;
using Arrk.TestSupport;

namespace Arrk.Jellyfin.Tests;

/// <summary>
/// Pure URL/route logic for <see cref="JellyfinLinkService"/>. These tests make no network calls.
/// </summary>
public class JellyfinLinkUrlTests
{
    static JellyfinLinkService Create(JellyfinConfig? jellyfin = null, RoutesConfig? routes = null)
    {
        JellyfinClient client = new("http://localhost:1", "admin", "admin", "test");
        return new JellyfinLinkService(
            client,
            new MutableOptionsMonitor<JellyfinConfig>(jellyfin ?? new JellyfinConfig()),
            new MutableOptionsMonitor<RoutesConfig>(routes ?? new RoutesConfig()),
            NullLogger<JellyfinLinkService>.Instance);
    }

    [Test]
    public async Task BaseUrl_DefaultsToLocalhostJellyfin()
    {
        await Assert.That(Create().BaseUrl).IsEqualTo("http://localhost/jellyfin");
    }

    [Test]
    public async Task BaseUrl_UsesConfiguredBaseUrl_AndTrimsTrailingSlash()
    {
        JellyfinLinkService service = Create(routes: new RoutesConfig { BaseUrl = "https://media.example.com/" });

        await Assert.That(service.BaseUrl).IsEqualTo("https://media.example.com/jellyfin");
    }

    [Test]
    public async Task BaseUrl_PrefersExplicitPublicUrl()
    {
        RoutesConfig routes = new() { PublicUrl = "https://media.example.com/" };

        await Assert.That(Create(routes: routes).BaseUrl).IsEqualTo("https://media.example.com/jellyfin");
    }

    [Test]
    public async Task BaseUrl_ResolvesFromBoundConfiguration()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["routes:publicUrl"] = "https://freebox.example.com"
            })
            .Build();

        ServiceCollection services = new();
        services.AddLogging();
        services.Configure<RoutesConfig>(configuration.GetSection("routes"));
        services.AddSingleton(new JellyfinClient("http://localhost:1", "admin", "admin", "test"));
        services.AddSingleton<JellyfinLinkService>();
        using ServiceProvider provider = services.BuildServiceProvider();

        JellyfinLinkService service = provider.GetRequiredService<JellyfinLinkService>();

        await Assert.That(service.BaseUrl).IsEqualTo("https://freebox.example.com/jellyfin");
    }

    [Test]
    public async Task BaseUrl_HonoursCustomRoute()
    {
        RoutesConfig routes = new();
        routes.CustomRoutes["jellyfin"] = "/media";

        await Assert.That(Create(routes: routes).BaseUrl).IsEqualTo("http://localhost/media");
    }

    [Test]
    public async Task IsAvailable_ReflectsEnabledAndProxy()
    {
        await Assert.That(Create().IsAvailable).IsTrue();
        await Assert.That(Create(jellyfin: new JellyfinConfig { Proxy = false }).IsAvailable).IsFalse();
        await Assert.That(Create(jellyfin: new JellyfinConfig { Enabled = false }).IsAvailable).IsFalse();
    }

    [Test]
    public async Task GetMovieUrl_ReturnsNull_WhenNoDownloadedFile()
    {
        TvMovie movie = new() { Title = "Scary Movie", TmdbId = 1273221, HasFile = false };

        await Assert.That(await Create().GetMovieUrlAsync(movie)).IsNull();
    }

    [Test]
    public async Task GetMovieUrl_ReturnsNull_WhenJellyfinUnavailable()
    {
        TvMovie movie = new()
        {
            Title = "Scary Movie",
            TmdbId = 1273221,
            HasFile = true,
            File = new MediaFileInfo { Path = "/movies/1273221/Scary Movie.mkv" }
        };

        await Assert.That(await Create(jellyfin: new JellyfinConfig { Proxy = false }).GetMovieUrlAsync(movie)).IsNull();
    }

    [Test]
    public async Task GetEpisodeUrl_ReturnsNull_WhenNotResolved()
    {
        await Assert.That(JellyfinLinkService.GetEpisodeUrl(null, 1, 1)).IsNull();

        TvSeriesLinks links = new("series-url", new Dictionary<(int, int), string> { [(1, 1)] = "ep-url" });
        await Assert.That(JellyfinLinkService.GetEpisodeUrl(links, 1, 2)).IsNull();
        await Assert.That(JellyfinLinkService.GetEpisodeUrl(links, 1, 1)).IsEqualTo("ep-url");
    }

    [Test]
    public async Task WatchUrl_IncludesArtworkAndEncodesQueryValues()
    {
        string url = JellyfinLinkService.WatchUrl(
            "abc123",
            "Game of Thrones S01E01",
            "/tv",
            "https://image.tmdb.org/t/p/w500/poster a.jpg");

        await Assert.That(url).Contains("/watch/abc123?");
        await Assert.That(url).Contains("title=Game%20of%20Thrones%20S01E01");
        await Assert.That(url).Contains("back=%2Ftv");
        await Assert.That(url).Contains("art=https%3A%2F%2Fimage.tmdb.org%2Ft%2Fp%2Fw500%2Fposter%20a.jpg");
    }

    [Test]
    public async Task WatchUrl_OmitsEmptySegments()
    {
        await Assert.That(JellyfinLinkService.WatchUrl("abc123")).IsEqualTo("/watch/abc123");
        await Assert.That(JellyfinLinkService.WatchUrl("abc123", null, null, "  ")).IsEqualTo("/watch/abc123");
    }

    [Test]
    public async Task ExtractFolderName_ReturnsFirstSegmentBelowMountRoot()
    {
        await Assert.That(JellyfinLinkService.ExtractFolderName("/movies/1273221/file.mkv", "/movies")).IsEqualTo("1273221");
        await Assert.That(JellyfinLinkService.ExtractFolderName("/movies/file.mkv", "/movies")).IsEqualTo("file.mkv");
        await Assert.That(JellyfinLinkService.ExtractFolderName("/shows/121361/S01/E01.mkv", "/movies")).IsNull();
        await Assert.That(JellyfinLinkService.ExtractFolderName(null, "/movies")).IsNull();
    }
}
