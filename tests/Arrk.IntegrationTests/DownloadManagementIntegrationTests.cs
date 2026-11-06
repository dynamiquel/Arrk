using Docker.DotNet;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Arrk.Core;
using Arrk.Core.Events;
using Arrk.Core.Models;
using Arrk.Core.Services;
using Arrk.DownloadManagement;
using Arrk.TestSupport;
using Arrk.Transmission;

namespace Arrk.IntegrationTests;

/// <summary>
/// Exercises download management against a real Transmission container: the cleanup must see the
/// live torrent list and leave healthy torrents alone.
/// </summary>
[NotInParallel("docker")]
public class DownloadManagementIntegrationTests
{
    static string _containerId = "";
    static string _networkId = "";
    static int _mappedPort;
    static TransmissionService? _service;

    const string ContainerImage = "lscr.io/linuxserver/transmission:latest";
    static readonly string ContainerName = $"stackarr-test-dlmgmt-{TestRun.Id}";

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
            ContainerImage, ContainerName, _networkId, 9091, env);

        bool ready = await ServiceContainerHelper.WaitForHttpReady(_mappedPort, TimeSpan.FromMinutes(3), "/transmission/web/");
        if (!ready) return;

        TransmissionClient client = new(
            $"http://localhost:{_mappedPort}",
            TransmissionServiceDefinition.DesiredUsername,
            TransmissionServiceDefinition.DesiredPassword);

        _service = new TransmissionService(
            client,
            new MutableOptionsMonitor<TorrentDownloaderConfig>(new TorrentDownloaderConfig()),
            new LoggerFactory().CreateLogger<TransmissionService>());
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
    public async Task ErrorTorrentCleanup_WithNoErrors_DoesNotDeleteAnything()
    {
        if (_service is null) return;

        int beforeCount = (await _service.GetTorrents("all")).Count;

        ErrorTorrentCleanup cleanup = new(
            _service,
            new MutableOptionsMonitor<TorrentDownloaderConfig>(new TorrentDownloaderConfig
            {
                Management = new DownloadManagementConfig
                {
                    Errors = new ErrorTorrentConfig
                    {
                        Enabled = true,
                        DeleteAfterMinutes = 0
                    }
                }
            }),
            new EventBus(),
            new FakeEventStore(),
            TimeProvider.System,
            new LoggerFactory().CreateLogger<ErrorTorrentCleanup>());

        await cleanup.CleanupErroredTorrents();

        int afterCount = (await _service.GetTorrents("all")).Count;
        await Assert.That(afterCount).IsEqualTo(beforeCount);
    }
}
