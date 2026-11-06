using System.Text.Json;
using Arrk.Jellyfin;

namespace Arrk.Jellyfin.Tests;

/// <summary>
/// Exercises <see cref="JellyfinService"/> against a real Jellyfin container: the first-run
/// wizard, library creation with TMDb metadata, the Arrk theme, idempotency and drift
/// correction. Skipped automatically when Docker is unavailable.
/// </summary>
[NotInParallel(JellyfinServerShared.ParallelKey)]
public class JellyfinServiceIntegrationTests
{
    static Task<JellyfinServer?> ServerAsync() => JellyfinServerShared.GetServerAsync();

    [Test]
    public async Task Configure_FromFreshServer_CompletesWizard()
    {
        JellyfinServer? server = await ServerAsync();
        if (server is null) return;

        await Assert.That(server.PublicInfoBeforeConfigure.StartupWizardCompleted).IsFalse();
        await Assert.That(server.FirstConfigureResult.StartupCompleted).IsTrue();

        JellyfinPublicSystemInfo after = await server.CreateClient().GetPublicSystemInfoAsync();
        await Assert.That(after.StartupWizardCompleted).IsTrue();
    }

    [Test]
    public async Task Configure_CreatesAllDefaultLibraries()
    {
        JellyfinServer? server = await ServerAsync();
        if (server is null) return;

        await Assert.That(server.FirstConfigureResult.LibrariesCreated).IsEqualTo(3);

        List<JellyfinVirtualFolder> folders = await server.CreateClient().GetVirtualFoldersAsync();
        await Assert.That(folders.Count(f => f.Name is "Movies" or "TV Shows" or "Music")).IsEqualTo(3);
    }

    [Test]
    public async Task Configure_LibrariesUseTmdbMetadata()
    {
        JellyfinServer? server = await ServerAsync();
        if (server is null) return;

        List<JellyfinVirtualFolder> folders = await server.CreateClient().GetVirtualFoldersAsync();

        JellyfinVirtualFolder movies = folders.Single(f => f.Name == "Movies");
        await Assert.That(movies.LibraryOptions.EnableInternetProviders).IsTrue();
        JellyfinTypeOptions movieType = movies.LibraryOptions.TypeOptions.Single(t => t.Type == "Movie");
        await Assert.That(movieType.MetadataFetchers).IsEquivalentTo(["TheMovieDb"]);
        await Assert.That(movieType.ImageFetchers).IsEquivalentTo(["TheMovieDb"]);

        JellyfinVirtualFolder shows = folders.Single(f => f.Name == "TV Shows");
        JellyfinTypeOptions seriesType = shows.LibraryOptions.TypeOptions.Single(t => t.Type == "Series");
        await Assert.That(seriesType.MetadataFetchers).IsEquivalentTo(["TheMovieDb"]);
    }

    [Test]
    public async Task Configure_AppliesServerNameAndTheme()
    {
        JellyfinServer? server = await ServerAsync();
        if (server is null) return;

        JellyfinClient client = server.CreateClient();
        JellyfinServerConfiguration configuration = await client.GetServerConfigurationAsync();
        JellyfinBrandingOptions branding = await client.GetBrandingAsync();

        await Assert.That(configuration.ServerName).IsEqualTo(server.Config.ServerName);
        await Assert.That(branding.CustomCss).IsEqualTo(JellyfinTheme.Css);
        await Assert.That(branding.LoginDisclaimer).Contains("sso/OIDC/Start/stackarr");
    }

    [Test]
    public async Task Configure_WithNvidia_EnablesNvencAndHardwareDecoding()
    {
        JellyfinServer? server = await ServerAsync();
        if (server is null) return;

        JellyfinConfig config = new()
        {
            ServerName = server.Config.ServerName,
            AdminUsername = server.Config.AdminUsername,
            AdminPassword = server.Config.AdminPassword,
            MetadataLanguage = server.Config.MetadataLanguage,
            MetadataCountry = server.Config.MetadataCountry,
            Nvidia = true
        };

        await server.CreateService(config).ConfigureAsync();

        JsonElement encoding = await server.CreateClient().GetEncodingConfigurationAsync();
        await Assert.That(encoding.GetProperty("HardwareAccelerationType").GetString()).IsEqualTo("nvenc");
        await Assert.That(encoding.GetProperty("EnableHardwareEncoding").GetBoolean()).IsTrue();
        await Assert.That(encoding.GetProperty("HardwareDecodingCodecs").EnumerateArray()
            .Select(c => c.GetString()).Contains("hevc")).IsTrue();
    }

    [Test]
    public async Task Configure_IsIdempotent()
    {
        JellyfinServer? server = await ServerAsync();
        if (server is null) return;

        // Settle any state left by other tests, then prove a further run changes nothing.
        await server.CreateService(server.Config).ConfigureAsync();
        JellyfinConfigureResult second = await server.CreateService(server.Config).ConfigureAsync();

        await Assert.That(second.StartupCompleted).IsFalse();
        await Assert.That(second.ServerUpdated).IsFalse();
        await Assert.That(second.LibrariesCreated).IsEqualTo(0);
        await Assert.That(second.LibrariesUpdated).IsEqualTo(0);
        await Assert.That(second.BrandingUpdated).IsFalse();
    }

    [Test]
    public async Task Configure_CorrectsDriftedLibraryMetadata()
    {
        JellyfinServer? server = await ServerAsync();
        if (server is null) return;

        JellyfinClient client = server.CreateClient();
        JellyfinVirtualFolder movies = (await client.GetVirtualFoldersAsync()).Single(f => f.Name == "Movies");

        // Introduce drift: strip TMDb from the movie type and disable internet providers.
        JellyfinLibraryOptions drifted = movies.LibraryOptions;
        drifted.EnableInternetProviders = false;
        foreach (JellyfinTypeOptions type in drifted.TypeOptions)
        {
            type.MetadataFetchers = [];
            type.MetadataFetcherOrder = [];
            type.ImageFetchers = [];
            type.ImageFetcherOrder = [];
        }
        await client.UpdateLibraryOptionsAsync(movies.ItemId, drifted);

        JellyfinConfigureResult result = await server.CreateService(server.Config).ConfigureAsync();
        await Assert.That(result.LibrariesUpdated).IsGreaterThan(0);

        JellyfinVirtualFolder restored = (await client.GetVirtualFoldersAsync()).Single(f => f.Name == "Movies");
        await Assert.That(restored.LibraryOptions.EnableInternetProviders).IsTrue();
        JellyfinTypeOptions movieType = restored.LibraryOptions.TypeOptions.Single(t => t.Type == "Movie");
        await Assert.That(movieType.MetadataFetchers).IsEquivalentTo(["TheMovieDb"]);
    }

    [Test]
    public async Task Configure_SkipsDisabledLibraries()
    {
        JellyfinServer? server = await ServerAsync();
        if (server is null) return;

        JellyfinConfig config = new()
        {
            ServerName = server.Config.ServerName,
            AdminUsername = server.Config.AdminUsername,
            AdminPassword = server.Config.AdminPassword
        };
        config.Libraries.Add(new JellyfinLibraryConfig
        {
            Name = "Documentaries",
            CollectionType = "movies",
            Path = "/movies",
            Enabled = false
        });

        JellyfinConfigureResult result = await server.CreateService(config).ConfigureAsync();

        await Assert.That(result.LibrariesCreated).IsEqualTo(0);
        List<JellyfinVirtualFolder> folders = await server.CreateClient().GetVirtualFoldersAsync();
        await Assert.That(folders.Any(f => f.Name == "Documentaries")).IsFalse();
    }

    [Test]
    public async Task Configure_UsesCustomCss_ThenRestoresBuiltInTheme()
    {
        JellyfinServer? server = await ServerAsync();
        if (server is null) return;

        JellyfinConfig custom = new() { CustomCss = "body { color: red; }" };
        JellyfinConfigureResult result = await server.CreateService(custom).ConfigureAsync();

        await Assert.That(result.BrandingUpdated).IsTrue();
        await Assert.That((await server.CreateClient().GetBrandingAsync()).CustomCss).IsEqualTo("body { color: red; }");

        await server.CreateService(server.Config).ConfigureAsync();
        await Assert.That((await server.CreateClient().GetBrandingAsync()).CustomCss).IsEqualTo(JellyfinTheme.Css);
    }

    [Test]
    public async Task Configure_IdentifiesItemsFromIdNamedFolders()
    {
        JellyfinServer? server = await ServerAsync();
        if (server is null || !server.HasMedia) return;

        await Assert.That(server.FirstConfigureResult.ItemsIdentified + server.ReconciledResult.ItemsIdentified)
            .IsGreaterThanOrEqualTo(2);

        JellyfinClient client = server.CreateClient();

        JellyfinItemsResult movies = await client.GetItemsAsync(new JellyfinItemQuery
        {
            Recursive = true,
            IncludeItemTypes = "Movie",
            Fields = ["ProviderIds", "Path"],
            Limit = 10
        });
        JellyfinItem? movie = movies.Items.FirstOrDefault(i => i.ProviderIds.ContainsKey("Tmdb"));
        await Assert.That(movie).IsNotNull();
        await Assert.That(movie!.Name).IsEqualTo("Scary Movie");
        await Assert.That(movie.ProviderIds["Tmdb"]).IsEqualTo(JellyfinServer.TmdbMovieId.ToString());

        JellyfinItemsResult series = await client.GetItemsAsync(new JellyfinItemQuery
        {
            Recursive = true,
            IncludeItemTypes = "Series",
            Fields = ["ProviderIds", "Path"],
            Limit = 10
        });
        JellyfinItem? show = series.Items.FirstOrDefault(i => i.ProviderIds.ContainsKey("Tvdb"));
        await Assert.That(show).IsNotNull();
        await Assert.That(show!.ProviderIds["Tvdb"]).IsEqualTo(JellyfinServer.TvdbSeriesId.ToString());
        await Assert.That(show.Name).IsNotEqualTo(JellyfinServer.TvdbSeriesId.ToString());
    }
}
