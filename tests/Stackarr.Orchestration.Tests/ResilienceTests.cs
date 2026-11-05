using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Stackarr.Core;
using Stackarr.Core.Models;
using Stackarr.Docker;
using Stackarr.TestSupport;
using ContainerState = Stackarr.Core.Enums.ContainerState;

namespace Stackarr.Orchestration.Tests;

[NotInParallel("docker")]
[RequiresDocker]
public class ResilienceTests
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
            try { await containerManager.RemoveContainer(id, force: true); } catch { }
        }
        foreach (string id in _networkIds)
        {
            try { await client.Networks.DeleteNetworkAsync(id); } catch { }
        }
        _containerIds.Clear();
        _networkIds.Clear();
    }

    [Test]
    public async Task ForceKilledContainer_CanBeCleanedUp()
    {
        using DockerClient client = OrchestrationTestHelper.CreateClient();
        ImageManager imageManager = OrchestrationTestHelper.CreateImageManager(client);
        ContainerManager containerManager = OrchestrationTestHelper.CreateContainerManager(client);

        await imageManager.PullImage("nginx:alpine");

        string containerId = await OrchestrationTestHelper.CreateContainerOnNetwork(
            client, "nginx:alpine", "stackarr-resil-force-kill", "bridge");
        _containerIds.Add(containerId);

        ContainerInfo? running = await containerManager.GetContainer("stackarr-resil-force-kill");
        await Assert.That(running!.State).IsEqualTo(ContainerState.Running);

        await containerManager.RemoveContainer(containerId, force: true);

        ContainerInfo? removed = await containerManager.GetContainer("stackarr-resil-force-kill");
        await Assert.That(removed).IsNull();
    }

    [Test]
    public async Task NetworkSurvivesContainerCrash()
    {
        using DockerClient client = OrchestrationTestHelper.CreateClient();
        ImageManager imageManager = OrchestrationTestHelper.CreateImageManager(client);
        NetworkManager networkManager = OrchestrationTestHelper.CreateNetworkManager(client);

        await imageManager.PullImage("nginx:alpine");
        string networkId = await networkManager.CreateOrGetNetwork("stackarr-resil-net-survive");
        _networkIds.Add(networkId);

        string cid1 = await OrchestrationTestHelper.CreateContainerOnNetwork(
            client, "nginx:alpine", "stackarr-resil-net-c1", networkId);
        _containerIds.Add(cid1);
        _containerIds.Add(cid1);

        ContainerManager containerManager = OrchestrationTestHelper.CreateContainerManager(client);
        ContainerInspectResponse inspect1 = await containerManager.InspectContainer(cid1);
        await Assert.That(inspect1.NetworkSettings.Networks.ContainsKey("stackarr-resil-net-survive")).IsTrue();

        await containerManager.RemoveContainer(cid1, force: true);

        bool exists = await networkManager.NetworkExists("stackarr-resil-net-survive");
        await Assert.That(exists).IsTrue();

        string cid2 = await OrchestrationTestHelper.CreateContainerOnNetwork(
            client, "nginx:alpine", "stackarr-resil-net-c2", networkId);
        _containerIds.Add(cid2);

        ContainerInspectResponse inspect2 = await containerManager.InspectContainer(cid2);
        await Assert.That(inspect2.NetworkSettings.Networks.ContainsKey("stackarr-resil-net-survive")).IsTrue();
    }

    [Test]
    public async Task NewContainerOnSameNetwork_AfterCrash_Works()
    {
        using DockerClient client = OrchestrationTestHelper.CreateClient();
        ImageManager imageManager = OrchestrationTestHelper.CreateImageManager(client);
        NetworkManager networkManager = OrchestrationTestHelper.CreateNetworkManager(client);

        await imageManager.PullImage("nginx:alpine");
        string networkId = await networkManager.CreateOrGetNetwork("stackarr-resil-reuse-net");
        _networkIds.Add(networkId);

        string cid1 = await OrchestrationTestHelper.CreateContainerOnNetwork(
            client, "nginx:alpine", "stackarr-resil-reuse-c1", networkId);
        _containerIds.Add(cid1);
        ContainerManager containerManager = OrchestrationTestHelper.CreateContainerManager(client);

        await containerManager.RemoveContainer(cid1, force: true);

        string cid2 = await OrchestrationTestHelper.CreateContainerOnNetwork(
            client, "nginx:alpine", "stackarr-resil-reuse-c2", networkId);
        _containerIds.Add(cid2);

        ContainerInfo? container = await containerManager.GetContainer("stackarr-resil-reuse-c2");
        await Assert.That(container).IsNotNull();
        await Assert.That(container!.State).IsEqualTo(ContainerState.Running);

        ContainerInspectResponse inspect = await containerManager.InspectContainer(cid2);
        await Assert.That(inspect.NetworkSettings.Networks.ContainsKey("stackarr-resil-reuse-net")).IsTrue();
    }

    [Test]
    public async Task MultipleContainersCrash_NetworkPersists()
    {
        using DockerClient client = OrchestrationTestHelper.CreateClient();
        ImageManager imageManager = OrchestrationTestHelper.CreateImageManager(client);
        NetworkManager networkManager = OrchestrationTestHelper.CreateNetworkManager(client);

        await imageManager.PullImage("nginx:alpine");
        string networkId = await networkManager.CreateOrGetNetwork("stackarr-resil-multi-crash");
        _networkIds.Add(networkId);

        List<string> cids = new List<string>();
        for (int i = 0; i < 3; i++)
        {
            string cid = await OrchestrationTestHelper.CreateContainerOnNetwork(
                client, "nginx:alpine", $"stackarr-resil-multi-c{i}", networkId);
            cids.Add(cid);
            _containerIds.Add(cid);
        }

        ContainerManager containerManager = OrchestrationTestHelper.CreateContainerManager(client);
        foreach (string cid in cids)
        {
            await containerManager.RemoveContainer(cid, force: true);
        }

        bool exists = await networkManager.NetworkExists("stackarr-resil-multi-crash");
        await Assert.That(exists).IsTrue();

        List<string> newCids = new List<string>();
        for (int i = 0; i < 3; i++)
        {
            string cid = await OrchestrationTestHelper.CreateContainerOnNetwork(
                client, "nginx:alpine", $"stackarr-resil-multi-new-c{i}", networkId);
            newCids.Add(cid);
            _containerIds.Add(cid);
        }

        foreach (string cid in newCids)
        {
            ContainerInfo? container = await containerManager.GetContainer(cid);
            await Assert.That(container).IsNotNull();
            await Assert.That(container!.State).IsEqualTo(ContainerState.Running);
        }
    }

    [Test]
    public async Task ContainerRestart_AfterForceKill_Succeeds()
    {
        using DockerClient client = OrchestrationTestHelper.CreateClient();
        ImageManager imageManager = OrchestrationTestHelper.CreateImageManager(client);
        ContainerManager containerManager = OrchestrationTestHelper.CreateContainerManager(client);

        await imageManager.PullImage("nginx:alpine");

        string containerId = await OrchestrationTestHelper.CreateContainerOnNetwork(
            client, "nginx:alpine", "stackarr-resil-restart-crash", "bridge");
        _containerIds.Add(containerId);

        await containerManager.StopContainer(containerId);
        ContainerInfo? stopped = await containerManager.GetContainer("stackarr-resil-restart-crash");
        await Assert.That(stopped!.State).IsEqualTo(ContainerState.Exited);

        await containerManager.RestartContainer(containerId);
        await Task.Delay(1000);
        ContainerInfo? restarted = await containerManager.GetContainer("stackarr-resil-restart-crash");
        await Assert.That(restarted!.State).IsEqualTo(ContainerState.Running);
    }

    [Test]
    public async Task RapidCreateDestroyCreate_DoesNotLeak()
    {
        using DockerClient client = OrchestrationTestHelper.CreateClient();
        ImageManager imageManager = OrchestrationTestHelper.CreateImageManager(client);
        ContainerManager containerManager = OrchestrationTestHelper.CreateContainerManager(client);

        await imageManager.PullImage("nginx:alpine");

        for (int cycle = 0; cycle < 3; cycle++)
        {
            string cid = await OrchestrationTestHelper.CreateContainerOnNetwork(
                client, "nginx:alpine", $"stackarr-resil-leak-{cycle}", "bridge");
            _containerIds.Add(cid);

            ContainerInfo? container = await containerManager.GetContainer($"stackarr-resil-leak-{cycle}");
            await Assert.That(container).IsNotNull();
            await Assert.That(container!.State).IsEqualTo(ContainerState.Running);

            await containerManager.RemoveContainer(cid, force: true);

            ContainerInfo? removed = await containerManager.GetContainer($"stackarr-resil-leak-{cycle}");
            await Assert.That(removed).IsNull();
        }
    }

    [Test]
    public async Task StoppedContainer_RemovedByForce_DoesNotLeaveOrphans()
    {
        using DockerClient client = OrchestrationTestHelper.CreateClient();
        ImageManager imageManager = OrchestrationTestHelper.CreateImageManager(client);
        ContainerManager containerManager = OrchestrationTestHelper.CreateContainerManager(client);

        await imageManager.PullImage("nginx:alpine");

        string cid = await OrchestrationTestHelper.CreateContainerOnNetwork(
            client, "nginx:alpine", "stackarr-resil-orphan", "bridge");
        _containerIds.Add(cid);

        await containerManager.StopContainer(cid);
        ContainerInfo? stopped = await containerManager.GetContainer("stackarr-resil-orphan");
        await Assert.That(stopped!.State).IsEqualTo(ContainerState.Exited);

        await containerManager.RemoveContainer(cid, force: true);
        _containerIds.Remove(cid);

        ContainerInfo? removed = await containerManager.GetContainer("stackarr-resil-orphan");
        await Assert.That(removed).IsNull();

        List<ContainerInfo> containers = await containerManager.ListContainers();
        await Assert.That(containers.Any(c => c.Name == "stackarr-resil-orphan")).IsFalse();
    }

    [Test]
    public async Task ConcurrentForceKill_AllContainersCleanedUp()
    {
        using DockerClient client = OrchestrationTestHelper.CreateClient();
        ImageManager imageManager = OrchestrationTestHelper.CreateImageManager(client);
        ContainerManager containerManager = OrchestrationTestHelper.CreateContainerManager(client);

        await imageManager.PullImage("nginx:alpine");

        List<string> cids = new List<string>();
        for (int i = 0; i < 3; i++)
        {
            string cid = await OrchestrationTestHelper.CreateContainerOnNetwork(
                client, "nginx:alpine", $"stackarr-resil-conc-crash-{i}", "bridge");
            cids.Add(cid);
            _containerIds.Add(cid);
        }

        IEnumerable<Task> removeTasks = cids.Select(id => containerManager.RemoveContainer(id, force: true));
        await Task.WhenAll(removeTasks);

        foreach (string cid in cids)
        {
            List<ContainerInfo> containers = await containerManager.ListContainers();
            await Assert.That(containers.Any(c => c.Id == cid)).IsFalse();
        }
    }

    [Test]
    public async Task HealthMonitor_ChecksHealth_WithoutError()
    {
        using DockerClient client = OrchestrationTestHelper.CreateClient();
        ContainerManager containerManager = OrchestrationTestHelper.CreateContainerManager(client);
        NetworkManager networkManager = OrchestrationTestHelper.CreateNetworkManager(client);
        ResourceInventory inventory = OrchestrationTestHelper.CreateResourceInventory(containerManager, networkManager);
        IDbContextFactory<StackarrDbContext> dbFactory = OrchestrationTestHelper.CreateInMemoryDbFactory();
        ILogger<HealthMonitor> logger = OrchestrationTestHelper.CreateLogger<Orchestration.HealthMonitor>();

        Orchestration.HealthMonitor monitor = new(
            containerManager, inventory, dbFactory, logger,
            checkInterval: TimeSpan.FromHours(1),
            retryDelay: TimeSpan.Zero,
            maxRetries: 0);

        await monitor.CheckAllHealth();
    }
}
