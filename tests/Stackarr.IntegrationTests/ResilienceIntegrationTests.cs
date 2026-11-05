using Docker.DotNet;
using Microsoft.Extensions.Logging;
using Stackarr.Core;
using Stackarr.Core.Enums;
using Stackarr.Core.Models;
using Stackarr.Core.Services;
using Stackarr.Docker;
using Stackarr.Orchestration;
using Stackarr.TestSupport;

using Stackarr.Core.Configuration;

namespace Stackarr.IntegrationTests;

[NotInParallel("docker")]
[RequiresDocker]
public class ResilienceIntegrationTests
{
    readonly List<string> _containerIds = [];
    readonly List<string> _networkIds = [];

    [After(Test)]
    public async Task Cleanup()
    {
        using DockerClient client = DockerHelper.CreateClient();

        foreach (string id in _containerIds)
        {
            try { await DockerTestCleanup.RemoveOwnedContainerAsync(client, id); } catch { }
        }
        foreach (string id in _networkIds)
        {
            try { await client.Networks.DeleteNetworkAsync(id); } catch { }
        }
        _containerIds.Clear();
        _networkIds.Clear();
    }

    [Test]
    public async Task ServiceRecovery_StoppedContainer_CanBeRestarted()
    {
        using DockerClient client = DockerHelper.CreateClient();
        ContainerManager containerManager = new ContainerManager(client);
        NetworkManager networkManager = new NetworkManager(client);
        ImageManager imageManager = new ImageManager(client);
        ServiceRegistry registry = TestHelper.CreateDefault();
        StackarrInstance stackarr = TestHelper.CreateStackarrInstance();
        DockerMetricsCollector metricsCollector = new DockerMetricsCollector(client, containerManager, networkManager, registry);
        ResourceInventory inventory = new ResourceInventory(containerManager, networkManager, metricsCollector,
            client, stackarr, new LoggerFactory().CreateLogger<ResourceInventory>());
        ServiceProvisioner provisioner = new ServiceProvisioner(containerManager, networkManager, imageManager, inventory,
            new MutableOptionsMonitor<StackarrConfig>(new()), stackarr, new LiveActivityRegistry(), new LoggerFactory().CreateLogger<ServiceProvisioner>());

        await imageManager.PullImage("nginx:alpine");
        // Test instances share the suite-wide test network, which is never deleted.
        string networkId = await networkManager.CreateOrGetNetwork(stackarr.NetworkName);

        string containerName = stackarr.GetContainerName("resil");
        string cid = await TestHelper.CreateContainerOnNetwork(
            client, "nginx:alpine", containerName, networkId,
            labels: new()
            {
                [ContainerLabels.Managed] = "true",
                [ContainerLabels.InstanceId] = stackarr.StackName,
                [ContainerLabels.Service] = "resilience-test"
            });
        _containerIds.Add(cid);

        bool running = await provisioner.EnsureContainerRunning(containerName);
        await Assert.That(running).IsTrue();

        await containerManager.StopContainer(cid);
        ContainerInfo? stopped = await TestHelper.WaitForContainerState(containerManager, containerName, ContainerState.Exited);
        await Assert.That(stopped!.State).IsEqualTo(ContainerState.Exited);

        bool recovered = await provisioner.EnsureContainerRunning(containerName);
        await Assert.That(recovered).IsTrue();

        ContainerInfo? afterRecovery = await TestHelper.WaitForContainerState(containerManager, containerName, ContainerState.Running);
        await Assert.That(afterRecovery!.State).IsEqualTo(ContainerState.Running);
    }

    [Test]
    public async Task ServiceRecovery_NonExistentContainer_ReturnsFalse()
    {
        using DockerClient client = DockerHelper.CreateClient();
        ContainerManager containerManager = new ContainerManager(client);
        NetworkManager networkManager = new NetworkManager(client);
        ImageManager imageManager = new ImageManager(client);
        ServiceRegistry registry = TestHelper.CreateDefault();
        StackarrInstance stackarr = TestHelper.CreateStackarrInstance();
        DockerMetricsCollector metricsCollector = new DockerMetricsCollector(client, containerManager, networkManager, registry);
        ResourceInventory inventory = new ResourceInventory(containerManager, networkManager, metricsCollector,
            client, stackarr, new LoggerFactory().CreateLogger<ResourceInventory>());
        ServiceProvisioner provisioner = new ServiceProvisioner(containerManager, networkManager, imageManager, inventory,
            new MutableOptionsMonitor<StackarrConfig>(new()), stackarr, new LiveActivityRegistry(), new LoggerFactory().CreateLogger<ServiceProvisioner>());

        bool result = await provisioner.EnsureContainerRunning("stackarr-does-not-exist");
        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task MetricsCollector_GetServiceMetrics_ReturnsMetricsForEachService()
    {
        using DockerClient client = DockerHelper.CreateClient();
        ContainerManager containerManager = new ContainerManager(client);
        NetworkManager networkManager = new NetworkManager(client);
        ImageManager imageManager = new ImageManager(client);
        ServiceRegistry registry = TestHelper.CreateDefault();
        DockerMetricsCollector metricsCollector = new DockerMetricsCollector(client, containerManager, networkManager, registry);

        await imageManager.PullImage("nginx:alpine");
        string networkId = await networkManager.CreateOrGetNetwork("stackarr-metrics-svc-net");
        _networkIds.Add(networkId);

        string cid1 = await TestHelper.CreateContainerOnNetwork(
            client, "nginx:alpine", "stackarr-metrics-svc-tv", networkId,
            labels: new()
            {
                [ContainerLabels.Managed] = "true",
                [ContainerLabels.Service] = "tv"
            });
        _containerIds.Add(cid1);

        string cid2 = await TestHelper.CreateContainerOnNetwork(
            client, "nginx:alpine", "stackarr-metrics-svc-indexer", networkId,
            labels: new()
            {
                [ContainerLabels.Managed] = "true",
                [ContainerLabels.Service] = "indexer"
            });
        _containerIds.Add(cid2);

        await Task.Delay(2000);

        List<string> services = new List<string> { "tv", "indexer" };
        List<ServiceDashboardInfo> metrics = await metricsCollector.GetServiceMetrics(services);

        await Assert.That(metrics.Count).IsEqualTo(2);
        await Assert.That(metrics[0].ServiceName).IsEqualTo("TV");
        await Assert.That(metrics[1].ServiceName).IsEqualTo("Indexer");
    }

    [Test]
    public async Task MetricsCollector_GetNetworkMetrics_ReturnsInterfaces()
    {
        using DockerClient client = DockerHelper.CreateClient();
        ContainerManager containerManager = new ContainerManager(client);
        NetworkManager networkManager = new NetworkManager(client);
        ServiceRegistry registry = TestHelper.CreateDefault();
        DockerMetricsCollector metricsCollector = new DockerMetricsCollector(client, containerManager, networkManager, registry);

        List<NetworkInterfaceMetrics> metrics = await metricsCollector.GetNetworkMetrics();

        await Assert.That(metrics).IsNotEmpty();
        NetworkInterfaceMetrics? eth0 = metrics.FirstOrDefault(m => m.InterfaceName == "eth0");
        if (eth0 != null)
        {
            await Assert.That(eth0.RxBytes).IsGreaterThanOrEqualTo(0);
            await Assert.That(eth0.TxBytes).IsGreaterThanOrEqualTo(0);
        }
    }

    [Test]
    public async Task MetricsCollector_GetContainerStats_ReturnsStatsForRunningContainer()
    {
        using DockerClient client = DockerHelper.CreateClient();
        ContainerManager containerManager = new ContainerManager(client);
        NetworkManager networkManager = new NetworkManager(client);
        ImageManager imageManager = new ImageManager(client);
        ServiceRegistry registry = TestHelper.CreateDefault();
        DockerMetricsCollector metricsCollector = new DockerMetricsCollector(client, containerManager, networkManager, registry);

        await imageManager.PullImage("nginx:alpine");
        string networkId = await networkManager.CreateOrGetNetwork("stackarr-metrics-net");
        _networkIds.Add(networkId);

        string cid = await TestHelper.CreateContainerOnNetwork(
            client, "nginx:alpine", "stackarr-metrics-test", networkId);
        _containerIds.Add(cid);

        await Task.Delay(2000);

        ContainerMetrics? stats = await metricsCollector.GetContainerStats(cid);
        await Assert.That(stats).IsNotNull();
        await Assert.That(stats!.MemoryUsageBytes).IsGreaterThan(0);
        await Assert.That(stats.MemoryLimitBytes).IsGreaterThan(0);
    }

    [Test]
    public async Task MultipleContainers_AllManaged_FilteredByInstance()
    {
        using DockerClient client = DockerHelper.CreateClient();
        ContainerManager containerManager = new ContainerManager(client);
        NetworkManager networkManager = new NetworkManager(client);
        ImageManager imageManager = new ImageManager(client);
        ServiceRegistry registry = TestHelper.CreateDefault();
        StackarrInstance stackarrA = TestHelper.CreateStackarrInstance("instance-a");
        StackarrInstance stackarrB = TestHelper.CreateStackarrInstance("instance-b");
        string instanceA = stackarrA.StackName;
        string instanceB = stackarrB.StackName;
        DockerMetricsCollector metricsCollectorA = new DockerMetricsCollector(client, containerManager, networkManager, registry);
        DockerMetricsCollector metricsCollectorB = new DockerMetricsCollector(client, containerManager, networkManager, registry);
        ResourceInventory inventoryA = new ResourceInventory(containerManager, networkManager, metricsCollectorA,
            client, stackarrA, new LoggerFactory().CreateLogger<ResourceInventory>());
        ResourceInventory inventoryB = new ResourceInventory(containerManager, networkManager, metricsCollectorB,
            client, stackarrB, new LoggerFactory().CreateLogger<ResourceInventory>());

        await imageManager.PullImage("nginx:alpine");
        string networkId = await networkManager.CreateOrGetNetwork("stackarr-inst-net");
        _networkIds.Add(networkId);

        string cid1 = await TestHelper.CreateContainerOnNetwork(
            client, "nginx:alpine", "stackarr-inst-a", networkId,
            labels: new()
            {
                [ContainerLabels.Managed] = "true",
                [ContainerLabels.InstanceId] = instanceA,
                [ContainerLabels.Service] = "test-a"
            });
        _containerIds.Add(cid1);

        string cid2 = await TestHelper.CreateContainerOnNetwork(
            client, "nginx:alpine", "stackarr-inst-b", networkId,
            labels: new()
            {
                [ContainerLabels.Managed] = "true",
                [ContainerLabels.InstanceId] = instanceB,
                [ContainerLabels.Service] = "test-b"
            });
        _containerIds.Add(cid2);

        List<ContainerInfo> ownedA = await inventoryA.GetOwnedContainers();
        List<ContainerInfo> ownedB = await inventoryB.GetOwnedContainers();

        await Assert.That(ownedA.Count).IsEqualTo(1);
        await Assert.That(ownedA[0].Name).IsEqualTo("stackarr-inst-a");

        await Assert.That(ownedB.Count).IsEqualTo(1);
        await Assert.That(ownedB[0].Name).IsEqualTo("stackarr-inst-b");
    }
}
