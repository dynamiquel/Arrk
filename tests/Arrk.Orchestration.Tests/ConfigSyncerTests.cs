using Docker.DotNet;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Arrk.Core;
using Arrk.Core.Configuration;
using Arrk.Core.Models;
using Arrk.Core.Services;
using Arrk.Docker;
using Arrk.TestSupport;

namespace Arrk.Orchestration.Tests;

[NotInParallel("docker")]
[RequiresDocker]
public class ConfigSyncerTests
{
    readonly List<string> _containerIds = [];
    readonly List<string> _networkIds = [];
    readonly List<string> _tempDirs = [];
    ResourceInventory? _cleanupInventory;

    const string TestImage = "nginx:alpine";

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
                ["services:slskd:enabled"] = musicEnabled.ToString().ToLower(),
                ["services:jellyfin:enabled"] = jellyfinEnabled.ToString().ToLower(),
                ["services:navidrome:enabled"] = navidromeEnabled.ToString().ToLower(),
                ["vpn:enabled"] = vpnEnabled.ToString().ToLower()
            })
            .Build();
    }

    [After(Test)]
    public async Task Cleanup()
    {
        using DockerClient client = OrchestrationTestHelper.CreateClient();

        foreach (string id in _containerIds)
        {
            try { await DockerTestCleanup.RemoveOwnedContainerAsync(client, id); } catch { }
        }

        // ConfigSyncer provisions the whole enabled stack itself, so those containers are not in
        // _containerIds. They carry this test's instance id, remove exactly those (and their
        // named config volumes), never anything else.
        if (_cleanupInventory is not null)
        {
            try
            {
                List<ContainerInfo> owned = await _cleanupInventory.GetOwnedContainers();
                foreach (ContainerInfo container in owned)
                    await DockerTestCleanup.RemoveOwnedContainerAsync(client, container.Id);
            }
            catch { }
            _cleanupInventory = null;
        }

        foreach (string id in _networkIds)
        {
            try { await client.Networks.DeleteNetworkAsync(id); } catch { }
        }
        _containerIds.Clear();
        _networkIds.Clear();

        foreach (string dir in _tempDirs)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }
        _tempDirs.Clear();
    }

    ArrkConfig CreateTestConfig(out string configBasePath)
    {
        string baseDir = Path.Combine(Path.GetTempPath(), $"stackarr-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(baseDir);
        _tempDirs.Add(baseDir);

        configBasePath = Path.Combine(baseDir, "config");
        Directory.CreateDirectory(configBasePath);

        foreach (string dir in new[] { "radarr", "sonarr", "lidarr", "prowlarr", "qbittorrent", "bazarr", "jellyfin", "navidrome" })
        {
            Directory.CreateDirectory(Path.Combine(configBasePath, dir));
        }

        string tvDir = Path.Combine(baseDir, "tv");
        string musicDir = Path.Combine(baseDir, "slskd");
        string downloadsDir = Path.Combine(baseDir, "downloads");

        Directory.CreateDirectory(tvDir);
        Directory.CreateDirectory(musicDir);
        Directory.CreateDirectory(downloadsDir);

        return new()
        {
            Storage = new()
            {
                Config = configBasePath,
                Media = baseDir,
                Tv = "tv",
                Music = "slskd",
                Downloads = downloadsDir
            }
        };
    }

    static ContainerSpec WithTestImage(ContainerSpec spec)
    {
        return new()
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
    }

    [Test]
    public async Task SyncConfigAsync_WithDefaultConfig_DoesNotThrow()
    {
        using DockerClient client = OrchestrationTestHelper.CreateClient();
        ContainerManager containerManager = OrchestrationTestHelper.CreateContainerManager(client);
        NetworkManager networkManager = OrchestrationTestHelper.CreateNetworkManager(client);
        ImageManager imageManager = OrchestrationTestHelper.CreateImageManager(client);
        ResourceInventory inventory = OrchestrationTestHelper.CreateResourceInventory(containerManager, networkManager, "sync-test");
        _cleanupInventory = inventory;
        ServiceProvisioner provisioner = OrchestrationTestHelper.CreateServiceProvisioner(containerManager, networkManager, imageManager, inventory, "sync-test");
        ArrkInstance arrk = OrchestrationTestHelper.CreateArrkInstance();

        MutableOptionsMonitor<ArrkConfig> configMonitor = new MutableOptionsMonitor<ArrkConfig>(new());
        IConfiguration configuration = CreateConfiguration();
        ServiceRegistry registry = TestHelper.CreateDefault();
        ConfigSyncer syncer = new ConfigSyncer(configMonitor, configuration, provisioner, registry, arrk, OrchestrationTestHelper.CreateLogger<ConfigSyncer>());

        await syncer.SyncConfigAsync(new());
    }

    [Test]
    public async Task SyncConfigAsync_WithTvDisabled_StopsRadarrAndSonarr()
    {
        using DockerClient client = OrchestrationTestHelper.CreateClient();
        ContainerManager containerManager = OrchestrationTestHelper.CreateContainerManager(client);
        NetworkManager networkManager = OrchestrationTestHelper.CreateNetworkManager(client);
        ImageManager imageManager = OrchestrationTestHelper.CreateImageManager(client);
        ResourceInventory inventory = OrchestrationTestHelper.CreateResourceInventory(containerManager, networkManager, "sync-test");
        _cleanupInventory = inventory;
        ServiceProvisioner provisioner = OrchestrationTestHelper.CreateServiceProvisioner(containerManager, networkManager, imageManager, inventory, "sync-test");
        ArrkInstance arrk = OrchestrationTestHelper.CreateArrkInstance();

        await imageManager.PullImage("nginx:alpine");

        ArrkConfig config = CreateTestConfig(out string configBasePath);
        IConfiguration configuration = CreateConfiguration();
        ServiceRegistry registry = TestHelper.CreateDefault();
        VolumePathResolver resolver = new VolumePathResolver(config.Storage);
        Service tvService = registry.GetRequired("tv");

        ContainerSpec radarrSpec = WithTestImage(tvService.Definition.BuildContainerSpecs(configuration, resolver, arrk).First(s => s.Name == arrk.GetContainerName("radarr")));
        string radarrId = await provisioner.ProvisionContainer(radarrSpec);
        _containerIds.Add(radarrId);

        ContainerSpec sonarrSpec = WithTestImage(tvService.Definition.BuildContainerSpecs(configuration, resolver, arrk).First(s => s.Name == arrk.GetContainerName("sonarr")));
        string sonarrId = await provisioner.ProvisionContainer(sonarrSpec);
        _containerIds.Add(sonarrId);

        ContainerInfo? radarrBefore = await containerManager.GetContainer(arrk.GetContainerName("radarr"));
        await Assert.That(radarrBefore).IsNotNull();
        await Assert.That(radarrBefore!.State).IsEqualTo(Core.Enums.ContainerState.Running);

        IConfiguration disabledConfiguration = CreateConfiguration(tvEnabled: false);
        MutableOptionsMonitor<ArrkConfig> configMonitor = new MutableOptionsMonitor<ArrkConfig>(new());
        ConfigSyncer syncer = new ConfigSyncer(configMonitor, disabledConfiguration, provisioner, registry, arrk, OrchestrationTestHelper.CreateLogger<ConfigSyncer>());

        await syncer.SyncConfigAsync(new());

        // On an otherwise-idle daemon the container remains stopped (Exited). If a real
        // Arrk instance is running on the same host, its orphan cleanup may remove the
        // test container entirely first, accept either outcome, but never "still running".
        await WaitUntilNotRunning(containerManager, arrk.GetContainerName("radarr"));
        ContainerInfo? radarr = await containerManager.GetContainer(arrk.GetContainerName("radarr"));
        await Assert.That(radarr == null || radarr.State == Core.Enums.ContainerState.Exited).IsTrue();

        await WaitUntilNotRunning(containerManager, arrk.GetContainerName("sonarr"));
        ContainerInfo? sonarr = await containerManager.GetContainer(arrk.GetContainerName("sonarr"));
        await Assert.That(sonarr == null || sonarr.State == Core.Enums.ContainerState.Exited).IsTrue();
    }

    static async Task WaitUntilNotRunning(ContainerManager containerManager, string name)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            ContainerInfo? container = await containerManager.GetContainer(name);
            if (container == null || container.State != Core.Enums.ContainerState.Running)
                return;
            await Task.Delay(250);
        }
    }

    [Test]
    public async Task GetEnabledServices_ReturnsCorrectServices()
    {
        IConfiguration configuration = CreateConfiguration(
            tvEnabled: true, musicEnabled: true,
            jellyfinEnabled: true, navidromeEnabled: false, vpnEnabled: false);

        ServiceRegistry registry = TestHelper.CreateDefault();
        List<string> services = ConfigSyncer.GetEnabledServices(configuration, registry);

        await Assert.That(services).Contains("tv");
        await Assert.That(services).Contains("slskd");
        await Assert.That(services).Contains("jellyfin");
        await Assert.That(services).Contains("reverse_proxy");
        await Assert.That(services).Contains("indexer");
        await Assert.That(services).Contains("torrent_downloader");
        await Assert.That(services).DoesNotContain("bazarr");

        await Assert.That(services).DoesNotContain("navidrome");
        await Assert.That(services).DoesNotContain("vpn");
    }

    [Test]
    public async Task GetDisabledServices_ReturnsCorrectServices()
    {
        IConfiguration configuration = CreateConfiguration(
            tvEnabled: false, musicEnabled: false,
            jellyfinEnabled: false, navidromeEnabled: true, vpnEnabled: true);

        ServiceRegistry registry = TestHelper.CreateDefault();
        List<string> services = ConfigSyncer.GetDisabledServices(configuration, registry);

        await Assert.That(services).Contains("tv");
        await Assert.That(services).Contains("slskd");
        await Assert.That(services).Contains("jellyfin");

        await Assert.That(services).DoesNotContain("navidrome");
        await Assert.That(services).DoesNotContain("vpn");
    }

    [Test]
    public async Task SyncConfigAsync_WithServicesToStopAndEnsure()
    {
        using DockerClient client = OrchestrationTestHelper.CreateClient();
        ContainerManager containerManager = OrchestrationTestHelper.CreateContainerManager(client);
        NetworkManager networkManager = OrchestrationTestHelper.CreateNetworkManager(client);
        ImageManager imageManager = OrchestrationTestHelper.CreateImageManager(client);
        ResourceInventory inventory = OrchestrationTestHelper.CreateResourceInventory(containerManager, networkManager, "sync-ensure");
        _cleanupInventory = inventory;
        ServiceProvisioner provisioner = OrchestrationTestHelper.CreateServiceProvisioner(containerManager, networkManager, imageManager, inventory, "sync-ensure");
        ArrkInstance arrk = OrchestrationTestHelper.CreateArrkInstance();

        ContainerInfo? existingLidarr = await containerManager.GetContainer(arrk.GetContainerName("lidarr"));
        if (existingLidarr != null)
            await containerManager.RemoveContainer(existingLidarr.Id, force: true);

        IConfiguration configuration = CreateConfiguration(musicEnabled: false);
        MutableOptionsMonitor<ArrkConfig> configMonitor = new MutableOptionsMonitor<ArrkConfig>(new());
        ServiceRegistry registry = TestHelper.CreateDefault();
        ConfigSyncer syncer = new ConfigSyncer(configMonitor, configuration, provisioner, registry, arrk, OrchestrationTestHelper.CreateLogger<ConfigSyncer>());

        await syncer.SyncConfigAsync(new());

        await Task.Delay(2000);

        ContainerInfo? lidarr = await containerManager.GetContainer(arrk.GetContainerName("lidarr"));
        await Assert.That(lidarr).IsNull();
    }

    [Test]
    public async Task GetEnabledServicesFromRegistry_ReturnsSameAsStatic()
    {
        IConfiguration configuration = CreateConfiguration(
            tvEnabled: true, musicEnabled: true,
            jellyfinEnabled: true, navidromeEnabled: false, vpnEnabled: false);

        using DockerClient client = OrchestrationTestHelper.CreateClient();
        ContainerManager containerManager = OrchestrationTestHelper.CreateContainerManager(client);
        NetworkManager networkManager = OrchestrationTestHelper.CreateNetworkManager(client);
        ImageManager imageManager = OrchestrationTestHelper.CreateImageManager(client);
        ResourceInventory inventory = OrchestrationTestHelper.CreateResourceInventory(containerManager, networkManager, "registry-test");
        ServiceProvisioner provisioner = OrchestrationTestHelper.CreateServiceProvisioner(containerManager, networkManager, imageManager, inventory, "registry-test");
        ArrkInstance arrk = OrchestrationTestHelper.CreateArrkInstance();

        ArrkConfig config = new ArrkConfig();
        ServiceRegistry registry = TestHelper.CreateDefault();
        ConfigSyncer syncer = new ConfigSyncer(
            new MutableOptionsMonitor<ArrkConfig>(config),
            configuration,
            provisioner,
            registry,
            arrk,
            OrchestrationTestHelper.CreateLogger<ConfigSyncer>());

        List<string> staticServices = ConfigSyncer.GetEnabledServices(configuration, registry);
        List<string> registryServices = syncer.GetEnabledServicesFromRegistry(configuration);

        HashSet<string> staticSet = staticServices.ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> registrySet = registryServices.ToHashSet(StringComparer.OrdinalIgnoreCase);

        await Assert.That(staticSet.IsSupersetOf(registrySet)).IsTrue();
        await Assert.That(registrySet.IsSupersetOf(staticSet)).IsTrue();
    }
}

