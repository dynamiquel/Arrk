using Docker.DotNet;
using Microsoft.EntityFrameworkCore;
using Stackarr.Core;
using Stackarr.Core.Enums;
using Stackarr.Core.Models;
using Stackarr.Core.Notifications;
using Stackarr.Docker;
using Stackarr.TestSupport;

namespace Stackarr.Orchestration.Tests;

[NotInParallel("docker")]
[RequiresDocker]
public class HealthMonitorTests
{
    readonly List<string> _containerIds = [];
    readonly List<string> _networkIds = [];

    [After(Test)]
    public async Task Cleanup()
    {
        using DockerClient client = OrchestrationTestHelper.CreateClient();
        ContainerManager containerManager = OrchestrationTestHelper.CreateContainerManager(client);

        foreach (string id in _containerIds)
        {
            try { await Stackarr.TestSupport.DockerTestCleanup.RemoveOwnedContainerAsync(client, id); } catch { }
        }
        foreach (string id in _networkIds)
        {
            try { await client.Networks.DeleteNetworkAsync(id); } catch { }
        }
        _containerIds.Clear();
        _networkIds.Clear();
    }

    [Test]
    public async Task CheckAllHealth_WithNoContainers_CompletesWithoutError()
    {
        using DockerClient client = OrchestrationTestHelper.CreateClient();
        ContainerManager containerManager = OrchestrationTestHelper.CreateContainerManager(client);
        NetworkManager networkManager = OrchestrationTestHelper.CreateNetworkManager(client);
        IDbContextFactory<StackarrDbContext> dbFactory = OrchestrationTestHelper.CreateInMemoryDbFactory(initializeSchema: true);
        StackarrInstance stackarr = OrchestrationTestHelper.CreateStackarrInstance();
        ResourceInventory inventory = OrchestrationTestHelper.CreateResourceInventory(containerManager, networkManager, stackarr.StackName);

        HealthMonitor monitor = new HealthMonitor(
            containerManager, inventory, dbFactory,
            OrchestrationTestHelper.CreateLogger<HealthMonitor>(),
            checkInterval: TimeSpan.FromHours(1),
            retryDelay: TimeSpan.Zero,
            maxRetries: 0);

        await monitor.CheckAllHealth();

        await Assert.That(monitor.LastContainerState).IsEmpty();
    }

    [Test]
    public async Task CheckAllHealth_WithRunningContainer_RecordsHealthy()
    {
        using DockerClient client = OrchestrationTestHelper.CreateClient();
        ContainerManager containerManager = OrchestrationTestHelper.CreateContainerManager(client);
        NetworkManager networkManager = OrchestrationTestHelper.CreateNetworkManager(client);
        ImageManager imageManager = OrchestrationTestHelper.CreateImageManager(client);
        IDbContextFactory<StackarrDbContext> dbFactory = OrchestrationTestHelper.CreateInMemoryDbFactory(initializeSchema: true);
        StackarrInstance stackarr = OrchestrationTestHelper.CreateStackarrInstance();
        ResourceInventory inventory = OrchestrationTestHelper.CreateResourceInventory(containerManager, networkManager, stackarr.StackName);

        await imageManager.PullImage("nginx:alpine");
        string networkId = await networkManager.CreateOrGetNetwork(stackarr.NetworkName);

        string containerName = $"stackarr-hrun-{stackarr.StackName}";
        string cid = await OrchestrationTestHelper.CreateContainerOnNetwork(
            client, "nginx:alpine", containerName, networkId,
            labels: new()
            {
                [ContainerLabels.Managed] = "true",
                [ContainerLabels.InstanceId] = stackarr.StackName,
                [ContainerLabels.Service] = "health-running"
            });
        _containerIds.Add(cid);

        HealthMonitor monitor = new HealthMonitor(
            containerManager, inventory, dbFactory,
            OrchestrationTestHelper.CreateLogger<HealthMonitor>(),
            checkInterval: TimeSpan.FromHours(1),
            retryDelay: TimeSpan.Zero,
            maxRetries: 0);

        await monitor.CheckAllHealth();

        await Assert.That(monitor.LastContainerState).ContainsKey(containerName);
        await Assert.That(monitor.LastContainerState[containerName]).IsEqualTo(ContainerState.Running);

        await using StackarrDbContext db = await dbFactory.CreateDbContextAsync();
        List<ServiceHealth> healthRecords = db.ServiceHealth.Where(h => h.ServiceName == containerName).ToList();
        await Assert.That(healthRecords).IsNotEmpty();
        await Assert.That(healthRecords[0].IsHealthy).IsTrue();
    }

    [Test]
    public async Task CheckAllHealth_WithStoppedContainer_RecordsUnhealthy()
    {
        using DockerClient client = OrchestrationTestHelper.CreateClient();
        ContainerManager containerManager = OrchestrationTestHelper.CreateContainerManager(client);
        NetworkManager networkManager = OrchestrationTestHelper.CreateNetworkManager(client);
        ImageManager imageManager = OrchestrationTestHelper.CreateImageManager(client);
        IDbContextFactory<StackarrDbContext> dbFactory = OrchestrationTestHelper.CreateInMemoryDbFactory(initializeSchema: true);
        StackarrInstance stackarr = OrchestrationTestHelper.CreateStackarrInstance();
        ResourceInventory inventory = OrchestrationTestHelper.CreateResourceInventory(containerManager, networkManager, stackarr.StackName);

        await imageManager.PullImage("nginx:alpine");
        string networkId = await networkManager.CreateOrGetNetwork(stackarr.NetworkName);

        string containerName = $"stackarr-hstop-{stackarr.StackName}";
        string cid = await OrchestrationTestHelper.CreateContainerOnNetwork(
            client, "nginx:alpine", containerName, networkId,
            labels: new()
            {
                [ContainerLabels.Managed] = "true",
                [ContainerLabels.InstanceId] = stackarr.StackName,
                [ContainerLabels.Service] = "health-stopped"
            });
        _containerIds.Add(cid);

        await containerManager.StopContainer(cid);
        // Docker can briefly report an intermediate "Removing" state before settling on Exited.
        await OrchestrationTestHelper.WaitForContainerState(containerManager, containerName, ContainerState.Exited);

        HealthMonitor monitor = new HealthMonitor(
            containerManager, inventory, dbFactory,
            OrchestrationTestHelper.CreateLogger<HealthMonitor>(),
            checkInterval: TimeSpan.FromHours(1),
            retryDelay: TimeSpan.Zero,
            maxRetries: 0);

        await monitor.CheckAllHealth();

        await Assert.That(monitor.LastContainerState).ContainsKey(containerName);
        // A stopped container must be recorded as not-running (Exited / Removing / Stopped),
        // never as healthy.
        await Assert.That(monitor.LastContainerState[containerName]).IsNotEqualTo(ContainerState.Running);
        await Assert.That(HealthMonitor.IsHealthyState(monitor.LastContainerState[containerName])).IsFalse();
    }

    [Test]
    public async Task CheckAllHealth_MultipleContainers_ChecksAll()
    {
        using DockerClient client = OrchestrationTestHelper.CreateClient();
        ContainerManager containerManager = OrchestrationTestHelper.CreateContainerManager(client);
        NetworkManager networkManager = OrchestrationTestHelper.CreateNetworkManager(client);
        ImageManager imageManager = OrchestrationTestHelper.CreateImageManager(client);
        IDbContextFactory<StackarrDbContext> dbFactory = OrchestrationTestHelper.CreateInMemoryDbFactory(initializeSchema: true);
        StackarrInstance stackarr = OrchestrationTestHelper.CreateStackarrInstance();
        ResourceInventory inventory = OrchestrationTestHelper.CreateResourceInventory(containerManager, networkManager, stackarr.StackName);

        await imageManager.PullImage("nginx:alpine");
        string networkId = await networkManager.CreateOrGetNetwork(stackarr.NetworkName);

        string name1 = $"stackarr-hm1-{stackarr.StackName}";
        string cid1 = await OrchestrationTestHelper.CreateContainerOnNetwork(
            client, "nginx:alpine", name1, networkId,
            labels: new()
            {
                [ContainerLabels.Managed] = "true",
                [ContainerLabels.InstanceId] = stackarr.StackName,
                [ContainerLabels.Service] = "multi-1"
            });
        _containerIds.Add(cid1);

        string name2 = $"stackarr-hm2-{stackarr.StackName}";
        string cid2 = await OrchestrationTestHelper.CreateContainerOnNetwork(
            client, "nginx:alpine", name2, networkId,
            labels: new()
            {
                [ContainerLabels.Managed] = "true",
                [ContainerLabels.InstanceId] = stackarr.StackName,
                [ContainerLabels.Service] = "multi-2"
            });
        _containerIds.Add(cid2);

        HealthMonitor monitor = new HealthMonitor(
            containerManager, inventory, dbFactory,
            OrchestrationTestHelper.CreateLogger<HealthMonitor>(),
            checkInterval: TimeSpan.FromHours(1),
            retryDelay: TimeSpan.Zero,
            maxRetries: 0);

        await monitor.CheckAllHealth();

        await Assert.That(monitor.LastContainerState.Count).IsEqualTo(2);
        await Assert.That(monitor.LastContainerState[name1]).IsEqualTo(ContainerState.Running);
        await Assert.That(monitor.LastContainerState[name2]).IsEqualTo(ContainerState.Running);
    }

    [Test]
    public async Task RecordHealthCheck_StoresHealthyRecordForRunningContainer()
    {
        using DockerClient client = OrchestrationTestHelper.CreateClient();
        ContainerManager containerManager = OrchestrationTestHelper.CreateContainerManager(client);
        NetworkManager networkManager = OrchestrationTestHelper.CreateNetworkManager(client);
        ImageManager imageManager = OrchestrationTestHelper.CreateImageManager(client);
        IDbContextFactory<StackarrDbContext> dbFactory = OrchestrationTestHelper.CreateInMemoryDbFactory(initializeSchema: true);
        StackarrInstance stackarr = OrchestrationTestHelper.CreateStackarrInstance();
        ResourceInventory inventory = OrchestrationTestHelper.CreateResourceInventory(containerManager, networkManager, stackarr.StackName);

        await imageManager.PullImage("nginx:alpine");
        string networkId = await networkManager.CreateOrGetNetwork(Name("record"));
        _networkIds.Add(networkId);
        string containerName = $"stackarr-hrec-{stackarr.StackName}";
        _containerIds.Add(await OrchestrationTestHelper.CreateContainerOnNetwork(
            client, "nginx:alpine", containerName, networkId,
            labels: new()
            {
                [ContainerLabels.Managed] = "true",
                [ContainerLabels.InstanceId] = stackarr.StackName,
                [ContainerLabels.Service] = "health-record"
            }));

        HealthMonitor monitor = new HealthMonitor(
            containerManager, inventory, dbFactory,
            OrchestrationTestHelper.CreateLogger<HealthMonitor>(),
            checkInterval: TimeSpan.FromHours(1),
            retryDelay: TimeSpan.Zero,
            maxRetries: 0);

        await monitor.CheckAllHealth();

        await using StackarrDbContext db = await dbFactory.CreateDbContextAsync();
        ServiceHealth? record = db.ServiceHealth.FirstOrDefault(h => h.ServiceName == containerName);
        await Assert.That(record).IsNotNull();
        await Assert.That(record!.IsHealthy).IsTrue();
    }

    [Test]
    public async Task CheckAllHealth_StateTransition_EmitsDownAndRecoveredNotifications()
    {
        using DockerClient client = OrchestrationTestHelper.CreateClient();
        ContainerManager containerManager = OrchestrationTestHelper.CreateContainerManager(client);
        NetworkManager networkManager = OrchestrationTestHelper.CreateNetworkManager(client);
        ImageManager imageManager = OrchestrationTestHelper.CreateImageManager(client);
        IDbContextFactory<StackarrDbContext> dbFactory = OrchestrationTestHelper.CreateInMemoryDbFactory(initializeSchema: true);
        StackarrInstance stackarr = OrchestrationTestHelper.CreateStackarrInstance();
        ResourceInventory inventory = OrchestrationTestHelper.CreateResourceInventory(containerManager, networkManager, stackarr.StackName);

        await imageManager.PullImage("nginx:alpine");
        string networkId = await networkManager.CreateOrGetNetwork(stackarr.NetworkName);

        string containerName = $"stackarr-hnotif-{stackarr.StackName}";
        string cid = await OrchestrationTestHelper.CreateContainerOnNetwork(
            client, "nginx:alpine", containerName, networkId,
            labels: new()
            {
                [ContainerLabels.Managed] = "true",
                [ContainerLabels.InstanceId] = stackarr.StackName,
                [ContainerLabels.Service] = "health-notify"
            });
        _containerIds.Add(cid);

        FakeEventStore events = new();
        HealthMonitor monitor = new HealthMonitor(
            containerManager, inventory, dbFactory,
            OrchestrationTestHelper.CreateLogger<HealthMonitor>(),
            checkInterval: TimeSpan.FromHours(1),
            retryDelay: TimeSpan.Zero,
            maxRetries: 0,
            eventStore: events);

        // First observation establishes the baseline and must not alert.
        await monitor.CheckAllHealth();
        await Assert.That(events.Notifications).IsEmpty();

        await containerManager.StopContainer(cid);
        await OrchestrationTestHelper.WaitForContainerState(containerManager, containerName, ContainerState.Exited);
        await monitor.CheckAllHealth();

        await Assert.That(events.Notifications.Any(n => n.Key == HealthNotificationTopics.ServiceDown)).IsTrue();

        await containerManager.StartContainer(cid);
        await OrchestrationTestHelper.WaitForContainerState(containerManager, containerName, ContainerState.Running);
        await monitor.CheckAllHealth();

        await Assert.That(events.Notifications.Any(n => n.Key == HealthNotificationTopics.ServiceRecovered)).IsTrue();
    }

    string Name(string suffix) => $"stackarr-{suffix}-{Guid.NewGuid():N}"[..40];
}
