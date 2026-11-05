using Docker.DotNet;
using Microsoft.EntityFrameworkCore;
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
public class OrchestrationIntegrationTests
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
    public async Task HealthMonitor_HealthChanged_FiresOnStateTransition()
    {
        using DockerClient client = DockerHelper.CreateClient();
        ContainerManager containerManager = new ContainerManager(client);
        NetworkManager networkManager = new NetworkManager(client);
        ImageManager imageManager = new ImageManager(client);
        IDbContextFactory<StackarrDbContext> dbFactory = CreateInMemoryDbFactory();
        ServiceRegistry registry = TestHelper.CreateDefault();
        StackarrInstance stackarr = TestHelper.CreateStackarrInstance();
        DockerMetricsCollector metricsCollector = new DockerMetricsCollector(client, containerManager, networkManager, registry);
        ResourceInventory inventory = new ResourceInventory(containerManager, networkManager, metricsCollector,
            client, stackarr, new LoggerFactory().CreateLogger<ResourceInventory>());

        await imageManager.PullImage("nginx:alpine");
        // Test instances share the suite-wide test network, which is never deleted.
        string networkId = await networkManager.CreateOrGetNetwork(stackarr.NetworkName);

        string containerName = $"{stackarr.ContainerPrefix}health-event";
        string cid = await TestHelper.CreateContainerOnNetwork(
            client, "nginx:alpine", containerName, networkId,
            labels: new()
            {
                [ContainerLabels.Managed] = "true",
                [ContainerLabels.InstanceId] = stackarr.StackName,
                [ContainerLabels.Service] = "health-event-test"
            });
        _containerIds.Add(cid);

        HealthMonitor monitor = new HealthMonitor(
            containerManager, inventory, dbFactory,
            new LoggerFactory().CreateLogger<HealthMonitor>(),
            checkInterval: TimeSpan.FromHours(1),
            retryDelay: TimeSpan.Zero,
            maxRetries: 0);

        List<(string Name, ContainerState State)> events = new List<(string Name, ContainerState State)>();
        monitor.HealthChanged += (name, state) => events.Add((name, state));

        await monitor.CheckAllHealth();
        await Assert.That(events).IsEmpty();

        await containerManager.StopContainer(cid);
        await monitor.CheckAllHealth();
        await Assert.That(events.Count).IsEqualTo(1);
        await Assert.That(events[0].Name).IsEqualTo(containerName);
        await Assert.That(events[0].State).IsEqualTo(ContainerState.Exited);
    }

    [Test]
    public async Task ServiceProvisioner_GetContainersByService_ReturnsMatchingContainers()
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

        string cid1 = await TestHelper.CreateContainerOnNetwork(
            client, "nginx:alpine", stackarr.GetContainerName("radarr"), networkId,
            labels: new()
            {
                [ContainerLabels.Managed] = "true",
                [ContainerLabels.InstanceId] = stackarr.StackName,
                [ContainerLabels.Service] = "radarr"
            });
        _containerIds.Add(cid1);

        string cid2 = await TestHelper.CreateContainerOnNetwork(
            client, "nginx:alpine", stackarr.GetContainerName("sonarr"), networkId,
            labels: new()
            {
                [ContainerLabels.Managed] = "true",
                [ContainerLabels.InstanceId] = stackarr.StackName,
                [ContainerLabels.Service] = "sonarr"
            });
        _containerIds.Add(cid2);

        List<ContainerInfo> radarrContainers = await provisioner.GetContainersByService("radarr");
        await Assert.That(radarrContainers.Count).IsEqualTo(1);
        await Assert.That(radarrContainers[0].Name).IsEqualTo(stackarr.GetContainerName("radarr"));

        List<ContainerInfo> sonarrContainers = await provisioner.GetContainersByService("sonarr");
        await Assert.That(sonarrContainers.Count).IsEqualTo(1);
        await Assert.That(sonarrContainers[0].Name).IsEqualTo(stackarr.GetContainerName("sonarr"));
    }

    [Test]
    public async Task DependencyOrder_InfrastructureBeforeDependents()
    {
        List<string> services = ["tv", "reverse_proxy", "slskd", "indexer", "torrent_downloader"];
        ServiceRegistry registry = TestHelper.CreateDefault();

        List<string> ordered = registry.All
            .Select(s => s.Definition.Key)
            .Where(services.Contains)
            .ToList();

        await Assert.That(ordered.IndexOf("reverse_proxy")).IsLessThan(ordered.IndexOf("tv"));
        await Assert.That(ordered.IndexOf("reverse_proxy")).IsLessThan(ordered.IndexOf("slskd"));
        await Assert.That(ordered.IndexOf("indexer")).IsLessThan(ordered.IndexOf("tv"));
        await Assert.That(ordered.IndexOf("torrent_downloader")).IsLessThan(ordered.IndexOf("tv"));
    }

    [Test]
    public async Task ServiceProvisioner_RemoveContainer_RemovesExistingContainer()
    {
        using DockerClient client = DockerHelper.CreateClient();
        ContainerManager containerManager = new ContainerManager(client);
        NetworkManager networkManager = new NetworkManager(client);
        ImageManager imageManager = new ImageManager(client);
        string instanceId = Guid.NewGuid().ToString("N");
        ServiceRegistry registry = TestHelper.CreateDefault();
        StackarrInstance stackarr = TestHelper.CreateStackarrInstance();
        DockerMetricsCollector metricsCollector = new DockerMetricsCollector(client, containerManager, networkManager, registry);
        ResourceInventory inventory = new ResourceInventory(containerManager, networkManager, metricsCollector,
            client, stackarr, new LoggerFactory().CreateLogger<ResourceInventory>());
        ServiceProvisioner provisioner = new ServiceProvisioner(containerManager, networkManager, imageManager, inventory,
            new MutableOptionsMonitor<StackarrConfig>(new()), stackarr, new LiveActivityRegistry(), new LoggerFactory().CreateLogger<ServiceProvisioner>());

        await imageManager.PullImage("nginx:alpine");

        ContainerSpec spec = new ContainerSpec
        {
            Image = "nginx:alpine",
            Name = stackarr.GetContainerName("remove-test")
        };

        string containerId = await provisioner.ProvisionContainer(spec);
        _containerIds.Add(containerId);

        ContainerInfo? before = await provisioner.GetContainerStatus(spec.Name);
        await Assert.That(before).IsNotNull();
        await Assert.That(before!.State).IsEqualTo(ContainerState.Running);

        await provisioner.RemoveContainer(spec.Name);
        _containerIds.Remove(containerId);

        ContainerInfo? after = await provisioner.GetContainerStatus(spec.Name);
        await Assert.That(after).IsNull();
    }

    static IDbContextFactory<StackarrDbContext> CreateInMemoryDbFactory()
    {
        DbContextOptions<StackarrDbContext> options = new DbContextOptionsBuilder<StackarrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new TestDbContextFactory(options);
    }
}

internal class TestDbContextFactory(DbContextOptions<StackarrDbContext> options) : IDbContextFactory<StackarrDbContext>
{
    public StackarrDbContext CreateDbContext() => new(options, []);

    public ValueTask<StackarrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new StackarrDbContext(options, []));
}
