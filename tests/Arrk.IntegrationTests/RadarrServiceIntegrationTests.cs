using Microsoft.Extensions.Logging;
using Arrk.Core.Models;
using Arrk.TestSupport;
using Arrk.Tvarr.Clients;
using Arrk.Tvarr.Models;
using Arrk.Tvarr.Services;

namespace Arrk.IntegrationTests;

/// <summary>
/// Integration tests that spin up a real Radarr container once for the whole class, extract
/// the real API key, and exercise authenticated API calls end-to-end through the container's
/// published port.
/// </summary>
[NotInParallel("docker")]
public class RadarrServiceIntegrationTests
{
    static string _containerId = "";
    static string _networkId = "";
    static int _mappedPort;
    static string _apiKey = "";
    static RadarrClient? _client;
    static RadarrService? _service;

    const string ContainerImage = "lscr.io/linuxserver/radarr:latest";
    static readonly string ContainerName = $"stackarr-test-radarr-{TestRun.Id}";
    const int ContainerPort = 7878;

    [Before(Class)]
    public static async Task Setup()
    {
        if (!await ServiceContainerHelper.IsDockerAvailable()) return;

        await ServiceContainerHelper.RemoveContainerByName(ContainerName);
        _networkId = await ServiceContainerHelper.CreateNetwork($"test-net-{Guid.NewGuid():N}");

        Dictionary<string, string> env = new Dictionary<string, string>
        {
            ["PUID"] = "1000",
            ["PGID"] = "1000",
            ["TZ"] = "Etc/UTC"
        };

        (_containerId, _mappedPort) = await ServiceContainerHelper.StartServiceContainer(
            ContainerImage, ContainerName, _networkId, ContainerPort, env);

        bool ready = await ServiceContainerHelper.WaitForHttpReady(_mappedPort, TimeSpan.FromMinutes(3), "/api/v3/system/status");
        if (!ready) return;

        _apiKey = await ServiceContainerHelper.GetRadarrApiKey(_containerId);

        // The test process runs on the host (not on the Docker network), so reach the
        // container through its published port rather than its container DNS name.
        _client = new($"http://localhost:{_mappedPort}", _apiKey);

        ILogger<RadarrService> logger = new LoggerFactory().CreateLogger<RadarrService>();
        _service = new(_client, logger);
    }

    [After(Class)]
    public static async Task Cleanup()
    {
        if (!string.IsNullOrEmpty(_containerId))
            await ServiceContainerHelper.RemoveContainer(_containerId);
        if (!string.IsNullOrEmpty(_networkId))
            await ServiceContainerHelper.DeleteNetwork(_networkId);
    }


    [Test]
    public async Task Connect_WithRealApiKey_Works()
    {
        if (_client == null) return;

        SystemStatus status = await _client.GetSystemStatus();

        await Assert.That(status).IsNotNull();
        await Assert.That(status.Version).IsNotEmpty();
    }

    [Test]
    public async Task Connect_WithBadApiKey_Fails()
    {
        RadarrClient badClient = new RadarrClient($"http://localhost:{_mappedPort}", "bad-key-12345");

        await Assert.That(async () => await badClient.GetSystemStatus())
            .Throws<HttpRequestException>();
    }

    [Test]
    public async Task GetMovies_WithRealApiKey_ReturnsList()
    {
        if (_service == null) return;

        List<TvMovie> movies = await _service.GetAllMovies();

        await Assert.That(movies).IsNotNull();
    }

    [Test]
    public async Task GetQualityProfiles_WithRealApiKey_ReturnsProfiles()
    {
        if (_client == null) return;

        List<QualityProfile> profiles = await _client.GetQualityProfiles();

        await Assert.That(profiles).IsNotNull();
        await Assert.That(profiles).IsNotEmpty();
        await Assert.That(profiles.First().Name).IsNotEmpty();
    }

    [Test]
    public async Task GetRootFolders_WithRealApiKey_ReturnsAddedFolder()
    {
        if (_client == null) return;

        string path = await ServiceContainerHelper.CreateWritableMediaDir(_containerId, $"/media/stackarr-test-{Guid.NewGuid():N}");
        await _client.AddRootFolder(path);

        List<RootFolder> folders = await _client.GetRootFolders();

        await Assert.That(folders).IsNotNull();
        await Assert.That(folders.Any(f => f.Path == path)).IsTrue();
    }

    [Test]
    public async Task SearchMovie_WithRealApiKey_ReturnsResults()
    {
        if (_client == null) return;

        List<MovieLookupDto> results = await _client.SearchMovie("Batman");

        await Assert.That(results).IsNotNull();
    }

    [Test]
    public async Task GetQueue_WithRealApiKey_ReturnsList()
    {
        if (_service == null) return;

        List<RadarrQueueItemDto> queue = await _service.GetQueue();

        await Assert.That(queue).IsNotNull();
    }

    [Test]
    public async Task GetDefaultQualityProfileId_WithRealApiKey_ReturnsValidId()
    {
        if (_service == null) return;

        int profileId = await _service.GetDefaultQualityProfileId();

        await Assert.That(profileId).IsGreaterThan(0);
    }

    [Test]
    public async Task GetDefaultRootFolder_WithRealApiKey_ReturnsValidPath()
    {
        if (_service == null) return;

        string rootFolder = await _service.GetDefaultRootFolder();

        await Assert.That(rootFolder).IsNotEmpty();
    }

    [Test]
    public async Task ConnectionToContainer_WorksEndToEnd()
    {
        if (_client == null || _service == null) return;

        string path = await ServiceContainerHelper.CreateWritableMediaDir(_containerId, $"/media/stackarr-test-e2e-{Guid.NewGuid():N}");
        await _client.AddRootFolder(path);

        SystemStatus status = await _client.GetSystemStatus();
        List<QualityProfile> profiles = await _client.GetQualityProfiles();
        List<RootFolder> folders = await _client.GetRootFolders();
        List<MovieDto> movies = await _client.GetMovies();
        List<RadarrQueueItemDto> queue = await _client.GetQueue();

        await Assert.That(status.Version).IsNotEmpty();
        await Assert.That(profiles).IsNotEmpty();
        await Assert.That(folders.Any(f => f.Path == path)).IsTrue();
        await Assert.That(movies).IsNotNull();
        await Assert.That(queue).IsNotNull();
    }

    [Test]
    public async Task Events_FireOnUpdate()
    {
        if (_service == null) return;

        bool eventFired = false;
        _service.MoviesUpdated += (_, _) => eventFired = true;

        List<TvMovie> movies = await _service.GetAllMovies();
        _service.NotifyMoviesUpdated(movies);

        await Assert.That(eventFired).IsTrue();
    }
}
