using Microsoft.Extensions.Logging;
using Stackarr.Core.Models;
using Stackarr.TestSupport;
using Stackarr.Tvarr.Clients;
using Stackarr.Tvarr.Models;
using Stackarr.Tvarr.Services;

namespace Stackarr.IntegrationTests;

/// <summary>
/// Integration tests that spin up a real Sonarr container once for the whole class, extract
/// the real API key, and exercise authenticated API calls end-to-end through the published port.
/// </summary>
[NotInParallel("docker")]
public class SonarrServiceIntegrationTests
{
    static string _containerId = "";
    static string _networkId = "";
    static int _mappedPort;
    static string _apiKey = "";
    static SonarrClient? _client;
    static SonarrService? _service;

    const string ContainerImage = "lscr.io/linuxserver/sonarr:latest";
    static readonly string ContainerName = $"stackarr-test-sonarr-{TestRun.Id}";

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
            ContainerImage, ContainerName, _networkId, 8989, env);

        bool ready = await ServiceContainerHelper.WaitForHttpReady(_mappedPort, TimeSpan.FromMinutes(3), "/api/v3/system/status");
        if (!ready) return;

        _apiKey = await ServiceContainerHelper.GetSonarrApiKey(_containerId);

        _client = new($"http://localhost:{_mappedPort}", _apiKey);

        ILogger<SonarrService> logger = new LoggerFactory().CreateLogger<SonarrService>();
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
    public async Task GetSystemStatus_WithRealApiKey_ReturnsVersion()
    {
        if (_client == null) return;

        SystemStatus status = await _client.GetSystemStatus();

        await Assert.That(status).IsNotNull();
        await Assert.That(status.Version).IsNotEmpty();
    }

    [Test]
    public async Task GetSystemStatus_WithEmptyApiKey_ThrowsUnauthorized()
    {
        SonarrClient badClient = new SonarrClient($"http://localhost:{_mappedPort}", "");

        await Assert.That(async () => await badClient.GetSystemStatus())
            .Throws<HttpRequestException>();
    }

    [Test]
    public async Task GetAllSeries_WithRealApiKey_ReturnsList()
    {
        if (_service == null) return;

        List<TvSeries> series = await _service.GetAllSeries();

        await Assert.That(series).IsNotNull();
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
    public async Task SearchSeries_WithRealApiKey_ReturnsResults()
    {
        if (_client == null) return;

        List<SeriesLookupDto> results = await _client.SearchSeries("Breaking Bad");

        await Assert.That(results).IsNotNull();
    }

    [Test]
    public async Task SearchSeries_WithBadApiKey_ThrowsUnauthorized()
    {
        SonarrClient badClient = new SonarrClient($"http://localhost:{_mappedPort}", "bad-key-12345");

        await Assert.That(async () => await badClient.SearchSeries("Breaking Bad"))
            .Throws<HttpRequestException>();
    }

    [Test]
    public async Task GetQueue_WithRealApiKey_ReturnsList()
    {
        if (_service == null) return;

        List<SonarrQueueItemDto> queue = await _service.GetQueue();

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
    public async Task GetTags_WithRealApiKey_ReturnsList()
    {
        if (_client == null) return;

        List<TagDto> tags = await _client.GetTags();

        await Assert.That(tags).IsNotNull();
    }

    [Test]
    public async Task GetCalendar_WithRealApiKey_ReturnsData()
    {
        if (_client == null) return;

        DateTime start = DateTime.UtcNow.AddDays(-30);
        DateTime end = DateTime.UtcNow.AddDays(30);

        List<SonarrCalendarDto> calendar = await _client.GetCalendar(start, end);

        await Assert.That(calendar).IsNotNull();
    }

    [Test]
    public async Task Events_FireOnUpdate()
    {
        if (_service == null) return;

        bool eventFired = false;
        _service.SeriesUpdated += (_, _) => eventFired = true;

        List<TvSeries> series = await _service.GetAllSeries();
        _service.NotifySeriesUpdated(series);

        await Assert.That(eventFired).IsTrue();
    }

    [Test]
    public async Task ConnectionToContainer_WorksEndToEnd()
    {
        if (_client == null) return;

        string path = await ServiceContainerHelper.CreateWritableMediaDir(_containerId, $"/media/stackarr-test-e2e-{Guid.NewGuid():N}");
        await _client.AddRootFolder(path);

        SystemStatus status = await _client.GetSystemStatus();
        List<QualityProfile> profiles = await _client.GetQualityProfiles();
        List<RootFolder> folders = await _client.GetRootFolders();
        List<SeriesDto> series = await _client.GetSeries();
        List<SonarrQueueItemDto> queue = await _client.GetQueue();

        await Assert.That(status.Version).IsNotEmpty();
        await Assert.That(profiles).IsNotEmpty();
        await Assert.That(folders.Any(f => f.Path == path)).IsTrue();
        await Assert.That(series).IsNotNull();
        await Assert.That(queue).IsNotNull();
    }
}
