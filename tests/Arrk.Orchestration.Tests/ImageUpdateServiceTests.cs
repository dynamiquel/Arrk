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
public class ImageUpdateServiceTests
{
    readonly List<string> _containerIds = [];
    readonly List<string> _networkIds = [];

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
        _containerIds.Clear();
        _networkIds.Clear();
    }

    static (ImageUpdateService service, global::Docker.DotNet.DockerClient client) CreateServiceWithClient(
        ArrkConfig? config = null,
        IConfiguration? configuration = null,
        ArrkInstance? arrk = null)
    {
        DockerClient client = OrchestrationTestHelper.CreateClient();
        ContainerManager containerManager = OrchestrationTestHelper.CreateContainerManager(client);
        NetworkManager networkManager = OrchestrationTestHelper.CreateNetworkManager(client);
        ImageManager imageManager = OrchestrationTestHelper.CreateImageManager(client);
        arrk ??= OrchestrationTestHelper.CreateArrkInstance();
        ResourceInventory inventory = OrchestrationTestHelper.CreateResourceInventory(containerManager, networkManager, arrk.StackName);
        ServiceRegistry registry = TestHelper.CreateDefault();
        ServiceProvisioner provisioner = OrchestrationTestHelper.CreateServiceProvisioner(containerManager, networkManager, imageManager, inventory, arrk.StackName);
        config ??= new();
        configuration ??= CreateConfiguration();

        MutableOptionsMonitor<ArrkConfig> optionsMonitor = new MutableOptionsMonitor<ArrkConfig>(config);

        ImageUpdateService service = new ImageUpdateService(
            containerManager,
            imageManager,
            inventory,
            registry,
            provisioner,
            optionsMonitor,
            configuration,
            arrk,
            OrchestrationTestHelper.CreateLogger<ImageUpdateService>());

        return (service, client);
    }

    [Test]
    public async Task ResolveDigestAsync_WithAlpine_ReturnsDigest()
    {
        (ImageUpdateService service, _) = CreateServiceWithClient();
        string? digest = await service.ResolveDigestAsync("alpine:latest");
        await Assert.That(digest).IsNotNull();
        await Assert.That(digest).StartsWith("sha256:");
    }

    [Test]
    public async Task GetImageUpdateInfoAsync_WithUnmanagedImage_ReturnsNoDependents()
    {
        (ImageUpdateService service, DockerClient client) = CreateServiceWithClient();
        ImageManager imageManager = OrchestrationTestHelper.CreateImageManager(client);

        // Use an image no other test provisions a managed service with: Docker suites from other
        // test assemblies run in parallel, and a concurrent nginx:alpine container labelled with a
        // service key would otherwise appear here as a spurious dependent.
        await imageManager.PullImage("busybox:latest");

        ImageUpdateInfo info = await service.GetImageUpdateInfoAsync("busybox:latest");

        await Assert.That(info.Image).IsEqualTo("busybox:latest");
        await Assert.That(info.RemoteDigest).IsNotNull();
        await Assert.That(info.DependentServices).IsEmpty();
    }

    [Test]
    public async Task GetImageUpdateInfoAsync_WithManagedContainer_FindsDependentServices()
    {
        (ImageUpdateService service, DockerClient client) = CreateServiceWithClient();
        ContainerManager containerManager = OrchestrationTestHelper.CreateContainerManager(client);
        NetworkManager networkManager = OrchestrationTestHelper.CreateNetworkManager(client);
        ImageManager imageManager = OrchestrationTestHelper.CreateImageManager(client);

        await imageManager.PullImage("nginx:alpine");
        string networkId = await networkManager.CreateOrGetNetwork("stackarr-test-net");
        _networkIds.Add(networkId);

        string containerId = await OrchestrationTestHelper.CreateContainerOnNetwork(
            client, "nginx:alpine", "stackarr-test-update-svc", networkId,
            labels: new()
            {
                [ContainerLabels.Managed] = "true",
                [ContainerLabels.Service] = "tv"
            });
        _containerIds.Add(containerId);

        ImageUpdateInfo info = await service.GetImageUpdateInfoAsync("nginx:alpine");

        await Assert.That(info.DependentServices).IsNotEmpty();
        await Assert.That(info.DependentServices).Contains("TV");
    }

    [Test]
    public async Task GetImageUpdateInfoAsync_WithExternalContainer_DetectsExternal()
    {
        (ImageUpdateService service, DockerClient client) = CreateServiceWithClient();
        NetworkManager networkManager = OrchestrationTestHelper.CreateNetworkManager(client);
        ImageManager imageManager = OrchestrationTestHelper.CreateImageManager(client);

        await imageManager.PullImage("nginx:alpine");
        string networkId = await networkManager.CreateOrGetNetwork("stackarr-test-net-ext");
        _networkIds.Add(networkId);

        string containerId = await OrchestrationTestHelper.CreateContainerOnNetwork(
            client, "nginx:alpine", "stackarr-test-external", networkId,
            labels: new());
        _containerIds.Add(containerId);

        ImageUpdateInfo info = await service.GetImageUpdateInfoAsync("nginx:alpine");

        await Assert.That(info.HasExternalContainers).IsTrue();
        await Assert.That(info.ExternalContainers).Contains("stackarr-test-external");
    }

    [Test]
    public async Task ForceRepullAsync_WithExternalContainers_ReturnsError()
    {
        (ImageUpdateService service, DockerClient client) = CreateServiceWithClient();
        NetworkManager networkManager = OrchestrationTestHelper.CreateNetworkManager(client);
        ImageManager imageManager = OrchestrationTestHelper.CreateImageManager(client);

        await imageManager.PullImage("nginx:alpine");
        string networkId = await networkManager.CreateOrGetNetwork("stackarr-test-net-repull");
        _networkIds.Add(networkId);

        string containerId = await OrchestrationTestHelper.CreateContainerOnNetwork(
            client, "nginx:alpine", "stackarr-test-repull-ext", networkId,
            labels: new());
        _containerIds.Add(containerId);

        ForceRepullResult result = await service.ForceRepullAsync("nginx:alpine");

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorMessage).IsNotNull();
        await Assert.That(result.ErrorMessage).Contains("external");
    }

    [Test]
    public async Task GetServiceUpdateInfosAsync_WithRunningContainer_ReturnsInfo()
    {
        IConfiguration configuration = CreateConfiguration(tvEnabled: true);
        ArrkInstance arrk = OrchestrationTestHelper.CreateArrkInstance();
        (ImageUpdateService service, DockerClient client) = CreateServiceWithClient(configuration: configuration, arrk: arrk);
        NetworkManager networkManager = OrchestrationTestHelper.CreateNetworkManager(client);
        ImageManager imageManager = OrchestrationTestHelper.CreateImageManager(client);

        await imageManager.PullImage("nginx:alpine");
        string networkId = await networkManager.CreateOrGetNetwork("stackarr-test-net-svc-update");
        _networkIds.Add(networkId);

        string containerId = await OrchestrationTestHelper.CreateContainerOnNetwork(
            client, "nginx:alpine", "stackarr-test-svc-update", networkId,
            labels: new()
            {
                [ContainerLabels.Managed] = "true",
                [ContainerLabels.InstanceId] = arrk.StackName,
                [ContainerLabels.Service] = "tv"
            });
        _containerIds.Add(containerId);

        List<ServiceUpdateInfo> infos = await service.GetServiceUpdateInfosAsync();

        ServiceUpdateInfo? tvInfo = infos.FirstOrDefault(i => i.ServiceKey == "tv");
        await Assert.That(tvInfo).IsNotNull();
    }

    [Test]
    public async Task GetImageUpdateInfoAsync_WithManagedContainer_ReportsDependentService()
    {
        IConfiguration configuration = CreateConfiguration(tvEnabled: true);
        ArrkInstance arrk = OrchestrationTestHelper.CreateArrkInstance();
        (ImageUpdateService service, DockerClient client) = CreateServiceWithClient(configuration: configuration, arrk: arrk);
        NetworkManager networkManager = OrchestrationTestHelper.CreateNetworkManager(client);
        ImageManager imageManager = OrchestrationTestHelper.CreateImageManager(client);

        await imageManager.PullImage("nginx:alpine");
        string networkName = $"stackarr-test-net-dep-{Guid.NewGuid():N}"[..40];
        string networkId = await networkManager.CreateOrGetNetwork(networkName);
        _networkIds.Add(networkId);

        string containerId = await OrchestrationTestHelper.CreateContainerOnNetwork(
            client, "nginx:alpine", $"stackarr-test-dep-{Guid.NewGuid():N}", networkId,
            labels: new()
            {
                [ContainerLabels.Managed] = "true",
                [ContainerLabels.InstanceId] = arrk.StackName,
                [ContainerLabels.Service] = "tv"
            });
        _containerIds.Add(containerId);

        ImageUpdateInfo info = await service.GetImageUpdateInfoAsync("nginx:alpine");

        await Assert.That(info.Image).IsEqualTo("nginx:alpine");
        await Assert.That(info.DependentServices).Contains("TV");
    }

}
