using Docker.DotNet;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Arrk.Core;
using Arrk.Core.Configuration;
using Arrk.Core.Enums;
using Arrk.Core.Models;
using Arrk.Core.Services;
using Arrk.Docker;
using Arrk.Orchestration;
using Arrk.TestSupport;

namespace Arrk.IntegrationTests;

/// <summary>
/// Proves that a service definition's container specs can actually be turned into running,
/// managed containers via <see cref="ServiceProvisioner"/>. One representative service per
/// media concept is provisioned with a real spec (volumes, env, labels, traefik) but an
/// nginx image so the test is fast and hermetic.
/// </summary>
[NotInParallel("docker")]
[RequiresDocker]
public class ServiceLaunchTests
{
    const string TestImage = "nginx:alpine";
    readonly List<string> _containerIds = [];

    static IConfiguration CreateConfiguration(
        bool tvEnabled = true,
        bool musicEnabled = true,
        bool jellyfinEnabled = true,
        bool navidromeEnabled = true,
        bool vpnEnabled = false)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["services:tv:enabled"] = tvEnabled.ToString().ToLower(),
                ["services:music:enabled"] = musicEnabled.ToString().ToLower(),
                ["services:jellyfin:enabled"] = jellyfinEnabled.ToString().ToLower(),
                ["services:navidrome:enabled"] = navidromeEnabled.ToString().ToLower(),
                ["vpn:enabled"] = vpnEnabled.ToString().ToLower()
            })
            .Build();
    }

    [After(Test)]
    public async Task Cleanup()
    {
        using DockerClient client = DockerHelper.CreateClient();
        foreach (string id in _containerIds)
        {
            try { await DockerTestCleanup.RemoveOwnedContainerAsync(client, id); } catch { }
        }
        _containerIds.Clear();
    }

    static ArrkConfig CreateTestConfig(out string configBasePath)
    {
        string baseDir = Path.Combine(Path.GetTempPath(), $"stackarr-launch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(baseDir);

        configBasePath = Path.Combine(baseDir, "config");
        Directory.CreateDirectory(configBasePath);

        foreach (string dir in new[] { "radarr", "sonarr", "prowlarr", "transmission", "bazarr", "jellyfin", "navidrome", "lidarr" })
        {
            Directory.CreateDirectory(Path.Combine(configBasePath, dir));
        }

        Directory.CreateDirectory(Path.Combine(baseDir, "tv"));
        Directory.CreateDirectory(Path.Combine(baseDir, "music"));
        Directory.CreateDirectory(Path.Combine(baseDir, "downloads"));

        return new()
        {
            Storage = new()
            {
                Config = configBasePath,
                Media = baseDir,
                Tv = "tv",
                Music = "music",
                Downloads = Path.Combine(baseDir, "downloads")
            }
        };
    }

    static ContainerSpec WithTestImage(ContainerSpec spec) => new()
    {
        Image = TestImage,
        Name = spec.Name,
        Volumes = [],
        Environment = spec.Environment,
        Ports = [],
        Labels = spec.Labels,
        NetworkName = spec.NetworkName,
        NetworkMode = spec.NetworkMode,
        RestartPolicy = spec.RestartPolicy,
        Traefik = spec.Traefik
    };

    static (ServiceProvisioner Provisioner, ContainerManager ContainerManager) CreateProvisionerPair()
    {
        DockerClient client = DockerHelper.CreateClient();
        ContainerManager containerManager = new ContainerManager(client);
        NetworkManager networkManager = new NetworkManager(client);
        ImageManager imageManager = new ImageManager(client);
        ServiceRegistry registry = TestHelper.CreateDefault();
        ArrkInstance arrk = TestHelper.CreateArrkInstance();
        DockerMetricsCollector metricsCollector = new DockerMetricsCollector(client, containerManager, networkManager, registry);
        ResourceInventory inventory = new ResourceInventory(containerManager, networkManager, metricsCollector,
            client, arrk, new LoggerFactory().CreateLogger<ResourceInventory>());
        ServiceProvisioner provisioner = new ServiceProvisioner(containerManager, networkManager, imageManager, inventory,
            new MutableOptionsMonitor<ArrkConfig>(new()), arrk, new LiveActivityRegistry(), new LoggerFactory().CreateLogger<ServiceProvisioner>());
        return (provisioner, containerManager);
    }

    static async Task ProvisionAndAssertRunning(
        List<string> trackedIds,
        string serviceKey,
        string implementation,
        string expectedServiceLabel)
    {
        ArrkConfig config = CreateTestConfig(out _);
        IConfiguration configuration = CreateConfiguration();
        ServiceRegistry registry = TestHelper.CreateDefault();
        ArrkInstance arrk = TestHelper.CreateArrkInstance();
        VolumePathResolver resolver = new VolumePathResolver(config.Storage);
        (ServiceProvisioner provisioner, _) = CreateProvisionerPair();

        Service service = registry.GetRequired(serviceKey);
        ContainerSpec spec = WithTestImage(service.Definition
            .BuildContainerSpecs(configuration, resolver, arrk)
            .First(s => s.Name == arrk.GetContainerName(implementation)));

        string containerId = await provisioner.ProvisionContainer(spec);
        trackedIds.Add(containerId);

        ContainerInfo? status = await provisioner.GetContainerStatus(spec.Name);
        await Assert.That(status).IsNotNull();
        await Assert.That(status!.State).IsEqualTo(ContainerState.Running);
        await Assert.That(status.Labels[ContainerLabels.Service]).IsEqualTo(expectedServiceLabel);
    }

    [Test]
    [Arguments("tv", "radarr", "tv")]
    [Arguments("tv", "sonarr", "tv")]
    [Arguments("indexer", "prowlarr", "indexer")]
    [Arguments("torrent_downloader", "transmission", "torrent_downloader")]
    public async Task Service_ProvisionsAndRuns(string serviceKey, string implementation, string expectedLabel)
    {
        await ProvisionAndAssertRunning(_containerIds, serviceKey, implementation, expectedLabel);
    }

    [Test]
    public async Task RealSpec_CreatesConfigDirectoriesAndRuns()
    {
        ArrkConfig config = CreateTestConfig(out string configBasePath);
        IConfiguration configuration = CreateConfiguration();
        ServiceRegistry registry = TestHelper.CreateDefault();
        ArrkInstance arrk = TestHelper.CreateArrkInstance();
        VolumePathResolver resolver = new VolumePathResolver(config.Storage);
        (ServiceProvisioner provisioner, _) = CreateProvisionerPair();

        ContainerSpec spec = registry.GetRequired("tv").Definition
            .BuildContainerSpecs(configuration, resolver, arrk)
            .First(s => s.Name == arrk.GetContainerName("radarr"));

        _containerIds.Add(await provisioner.ProvisionContainer(spec));

        ContainerInfo? status = await provisioner.GetContainerStatus(spec.Name);
        await Assert.That(status).IsNotNull();
        await Assert.That(status!.State).IsEqualTo(ContainerState.Running);

        await Assert.That(Directory.Exists(Path.Combine(configBasePath, "radarr"))).IsTrue();
    }

}
