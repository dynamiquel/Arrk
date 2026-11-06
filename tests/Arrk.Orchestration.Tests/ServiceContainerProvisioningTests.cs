using Docker.DotNet;
using Microsoft.Extensions.Configuration;
using Arrk.Configuration;
using Arrk.Core;
using Arrk.Core.Configuration;
using Arrk.Core.Models;
using Arrk.Core.Services;
using Arrk.Docker;
using Arrk.TestSupport;

namespace Arrk.Orchestration.Tests;

/// <summary>
/// End-to-end provisioning of real (nginx-substituted) containers built from actual
/// service definitions. Focused on proving that <see cref="ServiceProvisioner"/> turns a
/// <see cref="ContainerSpec"/> into a correctly-configured Docker container; per-service
/// spec content is covered by <see cref="EveryServiceTests"/>.
/// </summary>
[NotInParallel("docker")]
[RequiresDocker]
public class ServiceContainerProvisioningTests
{
    const string TestImage = "nginx:alpine";

    readonly List<string> _containerIds = [];
    readonly List<string> _networkIds = [];
    readonly List<string> _tempDirs = [];

    [After(Test)]
    public async Task Cleanup()
    {
        using DockerClient client = OrchestrationTestHelper.CreateClient();
        ContainerManager containerManager = OrchestrationTestHelper.CreateContainerManager(client);

        foreach (string id in _containerIds)
        {
            try { await containerManager.RemoveContainer(id, force: true); } catch { }
        }
        foreach (string id in _networkIds)
        {
            try { await client.Networks.DeleteNetworkAsync(id); } catch { }
        }
        foreach (string dir in _tempDirs)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }
        _containerIds.Clear();
        _networkIds.Clear();
        _tempDirs.Clear();
    }

    string CreateMediaRoot()
    {
        string baseDir = Path.Combine(Path.GetTempPath(), $"stackarr-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(baseDir);
        _tempDirs.Add(baseDir);
        Directory.CreateDirectory(Path.Combine(baseDir, "tv", "movies"));
        Directory.CreateDirectory(Path.Combine(baseDir, "tv", "shows"));
        Directory.CreateDirectory(Path.Combine(baseDir, "downloads"));
        Directory.CreateDirectory(Path.Combine(baseDir, "config"));
        return baseDir;
    }

    static StorageConfig TestStorage(string mediaRoot) => new()
    {
        Config = Path.Combine(mediaRoot, "config"),
        Media = mediaRoot,
        Tv = "$media/tv",
        Downloads = Path.Combine(mediaRoot, "downloads")
    };

    static ContainerSpec WithTestImage(ContainerSpec spec, string suffix) => new()
    {
        Image = TestImage,
        Name = $"{spec.Name}-{suffix}",
        Volumes = spec.Volumes,
        Environment = spec.Environment,
        Ports = spec.Ports,
        Labels = spec.Labels,
        NetworkName = spec.NetworkName,
        NetworkMode = spec.NetworkMode,
        RestartPolicy = spec.RestartPolicy,
        Traefik = spec.Traefik
    };

    static ContainerSpec FirstSpec(ServiceRegistry registry, string serviceKey, IConfiguration configuration, VolumePathResolver resolver, ArrkInstance arrk, string implementation)
    {
        return registry.GetRequired(serviceKey).Definition
            .BuildContainerSpecs(configuration, resolver, arrk)
            .First(s => s.Name == arrk.GetContainerName(implementation));
    }

    [Test]
    public async Task Provision_TvRadarrSpec_AppliesLabelsTraefikAndResolvedVolumes()
    {
        string suffix = Guid.NewGuid().ToString("N")[..8];
        using DockerClient client = OrchestrationTestHelper.CreateClient();
        ContainerManager containerManager = OrchestrationTestHelper.CreateContainerManager(client);
        NetworkManager networkManager = OrchestrationTestHelper.CreateNetworkManager(client);
        ImageManager imageManager = OrchestrationTestHelper.CreateImageManager(client);
        ServiceProvisioner provisioner = OrchestrationTestHelper.CreateServiceProvisioner(containerManager, networkManager, imageManager);
        ArrkInstance arrk = OrchestrationTestHelper.CreateArrkInstance();

        await imageManager.PullImage(TestImage);

        string mediaRoot = CreateMediaRoot();
        ArrkConfig config = new() { Storage = TestStorage(mediaRoot) };
        VolumePathResolver resolver = new(config.Storage);
        IConfiguration configuration = TestHelper.CreateServiceConfiguration();

        ContainerSpec spec = WithTestImage(FirstSpec(TestHelper.CreateDefault(), "tv", configuration, resolver, arrk, "radarr"), suffix);
        _containerIds.Add(await provisioner.ProvisionContainer(spec));

        ContainerInfo? status = await provisioner.GetContainerStatus(spec.Name);
        await Assert.That(status).IsNotNull();
        await Assert.That(status!.State).IsEqualTo(Core.Enums.ContainerState.Running);
        await Assert.That(status.Labels[ContainerLabels.Service]).IsEqualTo("tv");
        await Assert.That(status.Labels[ContainerLabels.Managed]).IsEqualTo("true");
        await Assert.That(status.Labels["traefik.http.routers.radarr.rule"]).IsEqualTo("PathPrefix(`/radarr`)");

        IReadOnlyList<VolumeMount> mounts = status.Mounts;
        await Assert.That(mounts.Any(m => m.ContainerPath == "/movies")).IsTrue();
        await Assert.That(mounts.Any(m => m.ContainerPath == "/downloads")).IsTrue();
    }

    [Test]
    public async Task TorrentDownloaderSpec_WithVpn_UsesGluetunNetworkAndKeepsTraefikMetadata()
    {
        // A VPN-routed container can only be started once the gluetun container exists
        // (NetworkMode = "service:<gluetun>"), so this asserts the generated spec only.
        using DockerClient client = OrchestrationTestHelper.CreateClient();
        ArrkInstance arrk = OrchestrationTestHelper.CreateArrkInstance();

        string mediaRoot = CreateMediaRoot();
        ArrkConfig config = new() { Storage = TestStorage(mediaRoot) };
        VolumePathResolver resolver = new(config.Storage);
        IConfiguration configuration = TestHelper.CreateServiceConfiguration(vpnEnabled: true, torrentVpn: true);

        ContainerSpec spec = FirstSpec(TestHelper.CreateDefault(), "torrent_downloader", configuration, resolver, arrk, "transmission");

        // Metadata retained for the file-provider route; NetworkMode set for the sidecar.
        await Assert.That(spec.Traefik).IsNotNull();
        await Assert.That(spec.NetworkMode).IsEqualTo(arrk.GetVpnNetworkMode());
    }

    [Test]
    public async Task Provision_MultipleServicesTogether_AllRunningAndManaged()
    {
        string suffix = Guid.NewGuid().ToString("N")[..8];
        using DockerClient client = OrchestrationTestHelper.CreateClient();
        ContainerManager containerManager = OrchestrationTestHelper.CreateContainerManager(client);
        NetworkManager networkManager = OrchestrationTestHelper.CreateNetworkManager(client);
        ImageManager imageManager = OrchestrationTestHelper.CreateImageManager(client);
        ServiceProvisioner provisioner = OrchestrationTestHelper.CreateServiceProvisioner(containerManager, networkManager, imageManager);
        ArrkInstance arrk = OrchestrationTestHelper.CreateArrkInstance();

        await imageManager.PullImage(TestImage);

        string mediaRoot = CreateMediaRoot();
        ArrkConfig config = new() { Storage = TestStorage(mediaRoot) };
        VolumePathResolver resolver = new(config.Storage);
        IConfiguration configuration = TestHelper.CreateServiceConfiguration();
        ServiceRegistry registry = TestHelper.CreateDefault();

        (string Impl, string Key)[] services =
        [
            ("radarr", "tv"),
            ("sonarr", "tv"),
            ("prowlarr", "indexer")
        ];

        List<string> names = [];
        foreach ((string impl, string key) in services)
        {
            ContainerSpec spec = WithTestImage(FirstSpec(registry, key, configuration, resolver, arrk, impl), suffix);
            _containerIds.Add(await provisioner.ProvisionContainer(spec));
            names.Add(spec.Name);
        }

        foreach (string name in names)
        {
            ContainerInfo? status = await provisioner.GetContainerStatus(name);
            await Assert.That(status).IsNotNull();
            await Assert.That(status!.State).IsEqualTo(Core.Enums.ContainerState.Running);
            await Assert.That(status.Labels[ContainerLabels.Managed]).IsEqualTo("true");
        }
    }

    [Test]
    public async Task Provision_Idempotent_DoesNotDuplicateContainer()
    {
        string suffix = Guid.NewGuid().ToString("N")[..8];
        using DockerClient client = OrchestrationTestHelper.CreateClient();
        ContainerManager containerManager = OrchestrationTestHelper.CreateContainerManager(client);
        NetworkManager networkManager = OrchestrationTestHelper.CreateNetworkManager(client);
        ImageManager imageManager = OrchestrationTestHelper.CreateImageManager(client);
        ServiceProvisioner provisioner = OrchestrationTestHelper.CreateServiceProvisioner(containerManager, networkManager, imageManager);
        ArrkInstance arrk = OrchestrationTestHelper.CreateArrkInstance();

        await imageManager.PullImage(TestImage);

        string mediaRoot = CreateMediaRoot();
        ArrkConfig config = new() { Storage = TestStorage(mediaRoot) };
        VolumePathResolver resolver = new(config.Storage);
        IConfiguration configuration = TestHelper.CreateServiceConfiguration();

        ContainerSpec spec = WithTestImage(FirstSpec(TestHelper.CreateDefault(), "indexer", configuration, resolver, arrk, "prowlarr"), suffix);
        string first = await provisioner.ProvisionContainer(spec);
        _containerIds.Add(first);
        string second = await provisioner.ProvisionContainer(spec);

        await Assert.That(first).IsEqualTo(second);
        List<ContainerInfo> matching = (await containerManager.ListContainers())
            .Where(c => c.Name == spec.Name).ToList();
        await Assert.That(matching.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Provision_StoppedThenReprovision_ReplacesAndStarts()
    {
        string suffix = Guid.NewGuid().ToString("N")[..8];
        using DockerClient client = OrchestrationTestHelper.CreateClient();
        ContainerManager containerManager = OrchestrationTestHelper.CreateContainerManager(client);
        NetworkManager networkManager = OrchestrationTestHelper.CreateNetworkManager(client);
        ImageManager imageManager = OrchestrationTestHelper.CreateImageManager(client);
        ServiceProvisioner provisioner = OrchestrationTestHelper.CreateServiceProvisioner(containerManager, networkManager, imageManager);
        ArrkInstance arrk = OrchestrationTestHelper.CreateArrkInstance();

        await imageManager.PullImage(TestImage);

        string mediaRoot = CreateMediaRoot();
        ArrkConfig config = new() { Storage = TestStorage(mediaRoot) };
        VolumePathResolver resolver = new(config.Storage);
        IConfiguration configuration = TestHelper.CreateServiceConfiguration();

        ContainerSpec spec = WithTestImage(FirstSpec(TestHelper.CreateDefault(), "bazarr", configuration, resolver, arrk, "bazarr"), suffix);
        _containerIds.Add(await provisioner.ProvisionContainer(spec));
        await provisioner.StopContainer(spec.Name);

        ContainerInfo? stopped = await provisioner.GetContainerStatus(spec.Name);
        await Assert.That(stopped!.State).IsEqualTo(Core.Enums.ContainerState.Exited);

        _containerIds.Add(await provisioner.ProvisionContainer(spec));
        ContainerInfo? running = await provisioner.GetContainerStatus(spec.Name);
        await Assert.That(running!.State).IsEqualTo(Core.Enums.ContainerState.Running);
    }

    [Test]
    public async Task Provision_SharedNetworkMode_OmittedEndpointsAndSucceeds()
    {
        // The Docker daemon rejects a create that specifies both a "container:<name>" network mode
        // and a networking config, so VPN-routed (Gluetun sidecar) containers must omit endpoints.
        string suffix = Guid.NewGuid().ToString("N")[..8];
        using DockerClient client = OrchestrationTestHelper.CreateClient();
        ContainerManager containerManager = OrchestrationTestHelper.CreateContainerManager(client);
        NetworkManager networkManager = OrchestrationTestHelper.CreateNetworkManager(client);
        ImageManager imageManager = OrchestrationTestHelper.CreateImageManager(client);
        ServiceProvisioner provisioner = OrchestrationTestHelper.CreateServiceProvisioner(containerManager, networkManager, imageManager);
        ArrkInstance arrk = OrchestrationTestHelper.CreateArrkInstance();

        await imageManager.PullImage(TestImage);

        // Stand in for Gluetun with the exact name the routing helper targets.
        ContainerSpec owner = new()
        {
            Image = TestImage,
            Name = arrk.GetVpnContainerName(),
            NetworkName = arrk.NetworkName
        };
        ContainerInfo? existingOwner = await containerManager.GetContainer(owner.Name);
        if (existingOwner != null)
            await containerManager.RemoveContainer(existingOwner.Id, force: true);
        string ownerId = await provisioner.ProvisionContainer(owner);
        _containerIds.Add(ownerId);

        string mediaRoot = CreateMediaRoot();
        ArrkConfig config = new() { Storage = TestStorage(mediaRoot) };
        VolumePathResolver resolver = new(config.Storage);
        IConfiguration configuration = TestHelper.CreateServiceConfiguration(vpnEnabled: true, torrentVpn: true);

        ContainerSpec child = WithTestImage(
            FirstSpec(TestHelper.CreateDefault(), "torrent_downloader", configuration, resolver, arrk, "transmission"), suffix);

        await Assert.That(child.NetworkMode).IsEqualTo(arrk.GetVpnNetworkMode());

        string childId = await provisioner.ProvisionContainer(child);
        _containerIds.Add(childId);

        ContainerInfo? childInfo = await provisioner.GetContainerStatus(child.Name);
        await Assert.That(childInfo).IsNotNull();
        await Assert.That(childInfo!.State).IsEqualTo(Core.Enums.ContainerState.Running);

        global::Docker.DotNet.Models.ContainerInspectResponse inspect = await containerManager.InspectContainer(childId);
        await Assert.That(inspect.HostConfig.NetworkMode).IsEqualTo($"container:{ownerId}");

        // Traefik labels must not be attached to a shared-namespace container.
        await Assert.That(inspect.Config.Labels.ContainsKey("traefik.enable")).IsFalse();
    }

    [Test]
    public async Task Provision_SharedNetworkParentRecreated_RecreatesChild()
    {
        // A dependent stores the parent's container ID. Recreating Gluetun changes that ID, so
        // the dependent must be recreated instead of being restarted against the removed ID.
        string suffix = Guid.NewGuid().ToString("N")[..8];
        using DockerClient client = OrchestrationTestHelper.CreateClient();
        ContainerManager containerManager = OrchestrationTestHelper.CreateContainerManager(client);
        NetworkManager networkManager = OrchestrationTestHelper.CreateNetworkManager(client);
        ImageManager imageManager = OrchestrationTestHelper.CreateImageManager(client);
        ServiceProvisioner provisioner = OrchestrationTestHelper.CreateServiceProvisioner(containerManager, networkManager, imageManager);
        ArrkInstance arrk = OrchestrationTestHelper.CreateArrkInstance();

        await imageManager.PullImage(TestImage);

        ContainerSpec owner = new()
        {
            Image = TestImage,
            Name = arrk.GetVpnContainerName(),
            NetworkName = arrk.NetworkName
        };
        ContainerInfo? existingOwner = await containerManager.GetContainer(owner.Name);
        if (existingOwner != null)
            await containerManager.RemoveContainer(existingOwner.Id, force: true);
        string firstOwnerId = await provisioner.ProvisionContainer(owner);
        _containerIds.Add(firstOwnerId);

        string mediaRoot = CreateMediaRoot();
        ArrkConfig config = new() { Storage = TestStorage(mediaRoot) };
        VolumePathResolver resolver = new(config.Storage);
        IConfiguration configuration = TestHelper.CreateServiceConfiguration(vpnEnabled: true, torrentVpn: true);

        ContainerSpec child = WithTestImage(
            FirstSpec(TestHelper.CreateDefault(), "torrent_downloader", configuration, resolver, arrk, "transmission"), suffix);

        string firstChildId = await provisioner.ProvisionContainer(child);
        _containerIds.Add(firstChildId);

        await provisioner.RemoveContainer(owner.Name);
        string secondOwnerId = await provisioner.ProvisionContainer(owner);
        _containerIds.Add(secondOwnerId);
        await Assert.That(secondOwnerId).IsNotEqualTo(firstOwnerId);

        string secondChildId = await provisioner.ProvisionContainer(child);
        _containerIds.Add(secondChildId);

        await Assert.That(secondChildId).IsNotEqualTo(firstChildId);
        global::Docker.DotNet.Models.ContainerInspectResponse inspect = await containerManager.InspectContainer(secondChildId);
        await Assert.That(inspect.HostConfig.NetworkMode).IsEqualTo($"container:{secondOwnerId}");
        await Assert.That(inspect.State.Running).IsTrue();
    }

    [Test]
    public async Task Provision_DeviceMapping_SplitsHostAndContainerPaths()
    {
        string suffix = Guid.NewGuid().ToString("N")[..8];
        using DockerClient client = OrchestrationTestHelper.CreateClient();
        ContainerManager containerManager = OrchestrationTestHelper.CreateContainerManager(client);
        NetworkManager networkManager = OrchestrationTestHelper.CreateNetworkManager(client);
        ImageManager imageManager = OrchestrationTestHelper.CreateImageManager(client);
        ServiceProvisioner provisioner = OrchestrationTestHelper.CreateServiceProvisioner(containerManager, networkManager, imageManager);
        ArrkInstance arrk = OrchestrationTestHelper.CreateArrkInstance();

        await imageManager.PullImage(TestImage);

        ContainerSpec spec = new()
        {
            Image = TestImage,
            Name = $"stackarr-device-{suffix}",
            NetworkName = arrk.NetworkName,
            Devices = ["/dev/null:/dev/null"]
        };

        string id = await provisioner.ProvisionContainer(spec);
        _containerIds.Add(id);

        global::Docker.DotNet.Models.ContainerInspectResponse inspect = await containerManager.InspectContainer(id);
        await Assert.That(inspect.HostConfig.Devices).HasSingleItem();
        await Assert.That(inspect.HostConfig.Devices[0].PathOnHost).IsEqualTo("/dev/null");
        await Assert.That(inspect.HostConfig.Devices[0].PathInContainer).IsEqualTo("/dev/null");
    }
}
