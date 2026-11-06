using Docker.DotNet;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Arrk.Core;
using Arrk.Core.Configuration;
using Arrk.Core.Models;
using Arrk.Core.Services;
using Arrk.Docker;
using Arrk.Orchestration;
using Arrk.TestSupport;

namespace Arrk.IntegrationTests;

/// <summary>
/// Integration coverage for the provisioning pipeline: building valid container specs from
/// every service definition, and driving <see cref="ServiceProvisioner"/> /
/// <see cref="ConfigSyncer"/> against a real Docker daemon. Spec *content* is covered by
/// the orchestration unit tests; here we care that the end-to-end path works with real Docker.
/// </summary>
[NotInParallel("docker")]
[RequiresDocker]
public class ProvisioningIntegrationTests
{
    readonly List<string> _containerIds = [];
    readonly List<string> _networkIds = [];
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

    ArrkConfig CreateTestConfig(out string configBasePath)
    {
        string baseDir = Path.Combine(Path.GetTempPath(), $"stackarr-intprov-{Guid.NewGuid():N}");
        Directory.CreateDirectory(baseDir);

        configBasePath = Path.Combine(baseDir, "config");
        Directory.CreateDirectory(configBasePath);

        foreach (string dir in new[] { "radarr", "sonarr", "lidarr", "prowlarr", "transmission", "bazarr", "jellyfin", "navidrome" })
        {
            Directory.CreateDirectory(Path.Combine(configBasePath, dir));
        }

        Directory.CreateDirectory(Path.Combine(baseDir, "tv"));
        Directory.CreateDirectory(Path.Combine(baseDir, "music"));
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

    static (ArrkInstance Arrk, VolumePathResolver Resolver) BuildResolvers(ArrkConfig config)
    {
        return (TestHelper.CreateArrkInstance(), new VolumePathResolver(config.Storage));
    }

    [Test]
    public async Task ServiceRegistry_EveryDefinitionProducesValidSpecs()
    {
        ArrkConfig config = CreateTestConfig(out _);
        IConfiguration configuration = CreateConfiguration();
        ServiceRegistry registry = TestHelper.CreateDefault();
        (ArrkInstance arrk, VolumePathResolver resolver) = BuildResolvers(config);

        foreach (Service service in registry.All)
        {
            IReadOnlyList<ContainerSpec> specs = service.Definition.BuildContainerSpecs(configuration, resolver, arrk);
            await Assert.That(specs).IsNotEmpty();

            foreach (ContainerSpec spec in specs)
            {
                await Assert.That(spec.Image).IsNotEmpty();
                await Assert.That(spec.Name).IsNotEmpty();
                await Assert.That(spec.Name).StartsWith(arrk.ContainerPrefix);
            }
        }
    }

    [Test]
    public async Task ServiceRegistry_EnabledServicesMatchConfig()
    {
        IConfiguration configuration = CreateConfiguration(
            tvEnabled: true, musicEnabled: false,
            jellyfinEnabled: false, navidromeEnabled: true, vpnEnabled: false);

        ServiceRegistry registry = TestHelper.CreateDefault();
        List<string> enabledKeys = registry.GetEnabled(configuration).Select(s => s.Definition.Key).ToList();

        await Assert.That(enabledKeys).Contains("tv");
        await Assert.That(enabledKeys).Contains("navidrome");
        await Assert.That(enabledKeys).Contains("indexer");
        await Assert.That(enabledKeys).Contains("torrent_downloader");
        await Assert.That(enabledKeys).DoesNotContain("bazarr");

        await Assert.That(enabledKeys).DoesNotContain("music");
        await Assert.That(enabledKeys).DoesNotContain("jellyfin");
        await Assert.That(enabledKeys).DoesNotContain("vpn");
    }

    [Test]
    public async Task ServiceRegistry_FindServiceByContainerLabel_ReturnsCorrectKeys()
    {
        ServiceRegistry registry = TestHelper.CreateDefault();

        await Assert.That(registry.FindServiceByContainerLabel("tv")?.Definition.Key).IsEqualTo("tv");
        await Assert.That(registry.FindServiceByContainerLabel("slskd")?.Definition.Key).IsEqualTo("slskd");
        await Assert.That(registry.FindServiceByContainerLabel("indexer")?.Definition.Key).IsEqualTo("indexer");
        await Assert.That(registry.FindServiceByContainerLabel("torrent_downloader")?.Definition.Key).IsEqualTo("torrent_downloader");
        await Assert.That(registry.FindServiceByContainerLabel("bazarr")?.Definition.Key).IsEqualTo("bazarr");
        await Assert.That(registry.FindServiceByContainerLabel("jellyfin")?.Definition.Key).IsEqualTo("jellyfin");
        await Assert.That(registry.FindServiceByContainerLabel("navidrome")?.Definition.Key).IsEqualTo("navidrome");
    }

    [Test]
    public async Task VolumePathResolver_ResolvesAllRelativeVolumes()
    {
        ArrkConfig config = CreateTestConfig(out _);
        VolumePathResolver resolver = new(config.Storage);

        await Assert.That(resolver.ResolveToHostPath(RelativeVolumes.Tv)).IsEqualTo(config.Storage.Tv);
        await Assert.That(resolver.ResolveToHostPath(RelativeVolumes.Music)).IsEqualTo(config.Storage.Music);
        await Assert.That(resolver.ResolveToHostPath(RelativeVolumes.Downloads)).IsEqualTo(config.Storage.Downloads);
        await Assert.That(resolver.ResolveToHostPath(RelativeVolumes.Media)).IsEqualTo(config.Storage.Media);
        await Assert.That(resolver.ResolveToHostPath(RelativeVolumes.DockerSocket)).EndsWith("docker.sock");

        await Assert.That(resolver.ResolveToHostPath(RelativeVolumes.Config + ":radarr")).Contains("radarr");
        await Assert.That(resolver.ResolveToContainerPath(RelativeVolumes.Config + ":radarr")).IsEqualTo("/config/radarr");
    }

    [Test]
    public async Task ServiceProvisioner_ProvisionsContainerFromRegistrySpec()
    {
        using DockerClient client = DockerHelper.CreateClient();
        ContainerManager containerManager = new(client);
        NetworkManager networkManager = new(client);
        ImageManager imageManager = new(client);
        ServiceRegistry registry = TestHelper.CreateDefault();
        DockerMetricsCollector metricsCollector = new(client, containerManager, networkManager, registry);
        ArrkInstance arrk = TestHelper.CreateArrkInstance();
        ResourceInventory inventory = new(containerManager, networkManager, metricsCollector, client, arrk,
            new LoggerFactory().CreateLogger<ResourceInventory>());
        ServiceProvisioner provisioner = new(containerManager, networkManager, imageManager, inventory,
            new MutableOptionsMonitor<ArrkConfig>(new()), arrk, new LiveActivityRegistry(), new LoggerFactory().CreateLogger<ServiceProvisioner>());

        ArrkConfig config = CreateTestConfig(out _);
        IConfiguration configuration = CreateConfiguration();
        VolumePathResolver resolver = new(config.Storage);

        ContainerSpec spec = WithTestImage(registry.GetRequired("tv").Definition
            .BuildContainerSpecs(configuration, resolver, arrk)
            .First(s => s.Name == arrk.GetContainerName("radarr")));

        string containerId = await provisioner.ProvisionContainer(spec);
        _containerIds.Add(containerId);

        ContainerInfo? status = await provisioner.GetContainerStatus(spec.Name);
        await Assert.That(status).IsNotNull();
        await Assert.That(status!.State).IsEqualTo(Core.Enums.ContainerState.Running);
        await Assert.That(status.Labels[ContainerLabels.Service]).IsEqualTo("tv");
    }

    [Test]
    public async Task ServiceProvisioner_ProvisionsMultipleServicesFromRegistry()
    {
        using DockerClient client = DockerHelper.CreateClient();
        ContainerManager containerManager = new(client);
        NetworkManager networkManager = new(client);
        ImageManager imageManager = new(client);
        ServiceRegistry registry = TestHelper.CreateDefault();
        DockerMetricsCollector metricsCollector = new(client, containerManager, networkManager, registry);
        ArrkInstance arrk = TestHelper.CreateArrkInstance();
        ResourceInventory inventory = new(containerManager, networkManager, metricsCollector, client, arrk,
            new LoggerFactory().CreateLogger<ResourceInventory>());
        ServiceProvisioner provisioner = new(containerManager, networkManager, imageManager, inventory,
            new MutableOptionsMonitor<ArrkConfig>(new()), arrk, new LiveActivityRegistry(), new LoggerFactory().CreateLogger<ServiceProvisioner>());

        ArrkConfig config = CreateTestConfig(out _);
        IConfiguration configuration = CreateConfiguration();
        VolumePathResolver resolver = new(config.Storage);

        (string Impl, string Key)[] services =
        [
            ("radarr", "tv"),
            ("sonarr", "tv"),
            ("prowlarr", "indexer"),
            ("transmission", "torrent_downloader")
        ];

        foreach ((string impl, string key) in services)
        {
            ContainerSpec spec = WithTestImage(registry.GetRequired(key).Definition
                .BuildContainerSpecs(configuration, resolver, arrk)
                .First(s => s.Name == arrk.GetContainerName(impl)));
            _containerIds.Add(await provisioner.ProvisionContainer(spec));
        }

        foreach ((string impl, string _) in services)
        {
            ContainerInfo? status = await provisioner.GetContainerStatus(arrk.GetContainerName(impl));
            await Assert.That(status).IsNotNull();
            await Assert.That(status!.State).IsEqualTo(Core.Enums.ContainerState.Running);
        }
    }

    [Test]
    public async Task ConfigSyncer_DisabledServices_StopsContainers()
    {
        using DockerClient client = DockerHelper.CreateClient();
        ContainerManager containerManager = new(client);
        NetworkManager networkManager = new(client);
        ImageManager imageManager = new(client);
        ServiceRegistry registry = TestHelper.CreateDefault();
        DockerMetricsCollector metricsCollector = new(client, containerManager, networkManager, registry);
        ArrkInstance arrk = TestHelper.CreateArrkInstance();
        ResourceInventory inventory = new(containerManager, networkManager, metricsCollector, client, arrk,
            new LoggerFactory().CreateLogger<ResourceInventory>());
        ServiceProvisioner provisioner = new(containerManager, networkManager, imageManager, inventory,
            new MutableOptionsMonitor<ArrkConfig>(new()), arrk, new LiveActivityRegistry(), new LoggerFactory().CreateLogger<ServiceProvisioner>());

        ArrkConfig config = CreateTestConfig(out _);
        IConfiguration configuration = CreateConfiguration();
        VolumePathResolver resolver = new(config.Storage);

        ContainerSpec radarrSpec = WithTestImage(registry.GetRequired("tv").Definition
            .BuildContainerSpecs(configuration, resolver, arrk)
            .First(s => s.Name == arrk.GetContainerName("radarr")));
        _containerIds.Add(await provisioner.ProvisionContainer(radarrSpec));

        _cleanupInventory = inventory;

        IConfiguration disabledConfiguration = CreateConfiguration(tvEnabled: false);
        ConfigSyncer syncer = new(new MutableOptionsMonitor<ArrkConfig>(new()), disabledConfiguration, provisioner, registry, arrk,
            new LoggerFactory().CreateLogger<ConfigSyncer>());

        await syncer.SyncConfigAsync(new());

        // Normally the container stays stopped (Exited); a concurrent real Arrk's orphan
        // cleanup may remove it entirely. Either is acceptable as long as it is not running.
        ContainerInfo? radarr = await WaitUntilNotRunning(containerManager, radarrSpec.Name);
        await Assert.That(radarr == null || radarr.State == Core.Enums.ContainerState.Exited).IsTrue();
    }

    static async Task<ContainerInfo?> WaitUntilNotRunning(ContainerManager containerManager, string name)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(20);
        ContainerInfo? container = null;
        while (DateTime.UtcNow < deadline)
        {
            container = await containerManager.GetContainer(name);
            if (container == null || container.State != Core.Enums.ContainerState.Running)
                return container;
            await Task.Delay(250);
        }
        return container;
    }

    [Test]
    public async Task ConfigSyncer_EnabledServices_EnsuresRunning()
    {
        using DockerClient client = DockerHelper.CreateClient();
        ContainerManager containerManager = new(client);
        NetworkManager networkManager = new(client);
        ImageManager imageManager = new(client);
        ServiceRegistry registry = TestHelper.CreateDefault();
        DockerMetricsCollector metricsCollector = new(client, containerManager, networkManager, registry);
        ArrkInstance arrk = TestHelper.CreateArrkInstance();
        ResourceInventory inventory = new(containerManager, networkManager, metricsCollector, client, arrk,
            new LoggerFactory().CreateLogger<ResourceInventory>());
        ServiceProvisioner provisioner = new(containerManager, networkManager, imageManager, inventory,
            new MutableOptionsMonitor<ArrkConfig>(new()), arrk, new LiveActivityRegistry(), new LoggerFactory().CreateLogger<ServiceProvisioner>());

        ArrkConfig config = CreateTestConfig(out _);
        IConfiguration configuration = CreateConfiguration();
        VolumePathResolver resolver = new(config.Storage);

        ContainerSpec radarrSpec = WithTestImage(registry.GetRequired("tv").Definition
            .BuildContainerSpecs(configuration, resolver, arrk)
            .First(s => s.Name == arrk.GetContainerName("radarr")));
        _containerIds.Add(await provisioner.ProvisionContainer(radarrSpec));

        await containerManager.StopContainer(radarrSpec.Name);

        ContainerInfo? stopped = await containerManager.GetContainer(radarrSpec.Name);
        await Assert.That(stopped!.State).IsEqualTo(Core.Enums.ContainerState.Exited);

        _cleanupInventory = inventory;

        IConfiguration enabledConfiguration = CreateConfiguration(tvEnabled: true);
        ConfigSyncer syncer = new(new MutableOptionsMonitor<ArrkConfig>(new()), enabledConfiguration, provisioner, registry, arrk,
            new LoggerFactory().CreateLogger<ConfigSyncer>());
        await syncer.SyncConfigAsync(new());

        ContainerInfo? restarted = await TestHelper.WaitForContainerState(
            containerManager, radarrSpec.Name, Core.Enums.ContainerState.Running);
        await Assert.That(restarted).IsNotNull();
        await Assert.That(restarted!.State).IsEqualTo(Core.Enums.ContainerState.Running);
    }

    [Test]
    public async Task ServiceRegistry_ProvisionOrder_PutsInfrastructureBeforeDependents()
    {
        ServiceRegistry registry = TestHelper.CreateDefault();
        IConfiguration configuration = CreateConfiguration();
        ArrkInstance arrk = TestHelper.CreateArrkInstance();
        ArrkConfig config = CreateTestConfig(out _);

        List<string> orderedKeys = registry.All
            .Where(s => s.Definition.IsEnabled(configuration))
            .Select(s => s.Definition.Key)
            .ToList();

        // The registry is built by ModuleLoader's topological sort using ProvisionBefore/After:
        // reverse_proxy first, then vpn, then the services that depend on them.
        await Assert.That(orderedKeys).Contains("reverse_proxy");
        await Assert.That(orderedKeys).Contains("tv");
        await Assert.That(orderedKeys).Contains("indexer");
        await Assert.That(orderedKeys).Contains("torrent_downloader");
        await Assert.That(orderedKeys.IndexOf("reverse_proxy")).IsLessThan(orderedKeys.IndexOf("tv"));
        await Assert.That(orderedKeys.IndexOf("indexer")).IsLessThan(orderedKeys.IndexOf("tv"));

        VolumePathResolver resolver = new(config.Storage);
        foreach (Service service in registry.GetEnabled(configuration))
        {
            IReadOnlyList<ContainerSpec> specs = service.Definition.BuildContainerSpecs(configuration, resolver, arrk);
            foreach (ContainerSpec spec in specs)
            {
                await Assert.That(spec.Name).StartsWith(arrk.ContainerPrefix);
            }
        }
    }
}

