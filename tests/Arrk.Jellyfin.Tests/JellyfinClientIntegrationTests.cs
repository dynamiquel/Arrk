using Arrk.Jellyfin;

namespace Arrk.Jellyfin.Tests;

/// <summary>
/// Exercises the real <see cref="JellyfinClient"/> against a real Jellyfin container. Skipped
/// automatically when Docker is unavailable.
/// </summary>
[NotInParallel(JellyfinServerShared.ParallelKey)]
public class JellyfinClientIntegrationTests
{
    static Task<JellyfinServer?> ServerAsync() => JellyfinServerShared.GetServerAsync();

    [Test]
    public async Task GetPublicSystemInfo_BeforeConfiguration_ReportsWizardIncomplete()
    {
        JellyfinServer? server = await ServerAsync();
        if (server is null) return;

        await Assert.That(server.PublicInfoBeforeConfigure.StartupWizardCompleted).IsFalse();
        await Assert.That(server.PublicInfoBeforeConfigure.Version).IsNotEmpty();
    }

    [Test]
    public async Task Authenticate_WithAdminCredentials_ReturnsToken()
    {
        JellyfinServer? server = await ServerAsync();
        if (server is null) return;

        JellyfinClient client = server.CreateClient();
        string token = await client.AuthenticateAsync();

        await Assert.That(token).IsNotEmpty();
        await Assert.That(client.HasToken).IsTrue();
    }

    [Test]
    public async Task Authenticate_WithWrongPassword_Throws()
    {
        JellyfinServer? server = await ServerAsync();
        if (server is null) return;

        JellyfinClient client = server.CreateClient(password: "not-the-password");

        await Assert.That(async () => await client.AuthenticateAsync()).Throws<HttpRequestException>();
    }

    [Test]
    public async Task AuthenticatedRequest_WithWrongPassword_Throws()
    {
        JellyfinServer? server = await ServerAsync();
        if (server is null) return;

        JellyfinClient client = server.CreateClient(password: "not-the-password");

        await Assert.That(async () => await client.GetServerConfigurationAsync()).Throws<HttpRequestException>();
    }

    [Test]
    public async Task GetServerConfiguration_ReturnsConfiguredServerName()
    {
        JellyfinServer? server = await ServerAsync();
        if (server is null) return;

        JellyfinServerConfiguration configuration = await server.CreateClient().GetServerConfigurationAsync();

        await Assert.That(configuration.ServerName).IsEqualTo(server.Config.ServerName);
        await Assert.That(configuration.PreferredMetadataLanguage).IsEqualTo(server.Config.MetadataLanguage);
        await Assert.That(configuration.MetadataCountryCode).IsEqualTo(server.Config.MetadataCountry);
    }

    [Test]
    public async Task GetBranding_ReturnsArrkTheme()
    {
        JellyfinServer? server = await ServerAsync();
        if (server is null) return;

        JellyfinBrandingOptions branding = await server.CreateClient().GetBrandingAsync();

        await Assert.That(branding.CustomCss).IsEqualTo(JellyfinTheme.Css);
        await Assert.That(branding.LoginDisclaimer).Contains("sso/OIDC/Start/stackarr");
    }

    [Test]
    public async Task GetVirtualFolders_ReturnsConfiguredLibraries()
    {
        JellyfinServer? server = await ServerAsync();
        if (server is null) return;

        List<JellyfinVirtualFolder> folders = await server.CreateClient().GetVirtualFoldersAsync();

        await Assert.That(folders.Any(f => f.Name == "Movies" && f.Locations.Contains("/movies"))).IsTrue();
        await Assert.That(folders.Any(f => f.Name == "TV Shows" && f.Locations.Contains("/shows"))).IsTrue();
        await Assert.That(folders.Any(f => f.Name == "Music" && f.Locations.Contains("/music"))).IsTrue();
    }

    [Test]
    public async Task GetUsers_ContainsAdmin()
    {
        JellyfinServer? server = await ServerAsync();
        if (server is null) return;

        List<JellyfinUser> users = await server.CreateClient().GetUsersAsync();

        JellyfinUser? admin = users.FirstOrDefault(u => u.Name == JellyfinServer.AdminUsername);
        await Assert.That(admin).IsNotNull();
        await Assert.That(admin!.Policy?.IsAdministrator).IsTrue();
    }

    [Test]
    public async Task AuthenticateByName_RoundTrips()
    {
        JellyfinServer? server = await ServerAsync();
        if (server is null) return;

        JellyfinAuthenticationResult result = await server.CreateClient()
            .AuthenticateByNameAsync(JellyfinServer.AdminUsername, JellyfinServer.AdminPassword);

        await Assert.That(result.AccessToken).IsNotEmpty();
        await Assert.That(result.User!.Name).IsEqualTo(JellyfinServer.AdminUsername);
    }

    [Test]
    public async Task GetItems_ByProviderId_ReturnsDirectMatch()
    {
        JellyfinServer? server = await ServerAsync();
        if (server is null || !server.HasMedia) return;

        JellyfinItemsResult result = await server.CreateClient().GetItemsAsync(new JellyfinItemQuery
        {
            Recursive = true,
            IncludeItemTypes = "Movie",
            AnyProviderIdEquals = $"tmdb.{JellyfinServer.TmdbMovieId}",
            Fields = ["ProviderIds"],
            Limit = 5
        });

        await Assert.That(result.Items.Count).IsGreaterThan(0);
        await Assert.That(result.Items[0].ProviderIds["Tmdb"]).IsEqualTo(JellyfinServer.TmdbMovieId.ToString());
    }

    [Test]
    public async Task RemoteSearch_ByProviderId_ReturnsMatch()
    {
        JellyfinServer? server = await ServerAsync();
        if (server is null) return;

        List<JellyfinRemoteSearchResult> matches = await server.CreateClient()
            .RemoteSearchAsync("Movie", "Tmdb", JellyfinServer.TmdbMovieId.ToString());

        await Assert.That(matches.Count).IsGreaterThan(0);
        await Assert.That(matches[0].ProviderIds["Tmdb"]).IsEqualTo(JellyfinServer.TmdbMovieId.ToString());
    }
}
