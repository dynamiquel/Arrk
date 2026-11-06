using Docker.DotNet;
using Docker.DotNet.Models;
using Arrk.Core;
using Arrk.Core.Models;
using Arrk.Docker;
using Arrk.TestSupport;
using TUnit.Core;

namespace Arrk.Orchestration.Tests;

[NotInParallel("docker")]
[RequiresDocker]
public class ResourceInventoryTests
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
    public async Task GetOwnedContainers_ReturnsOnlyLabeledContainers()
    {
        using DockerClient client = OrchestrationTestHelper.CreateClient();
        ContainerManager containerManager = OrchestrationTestHelper.CreateContainerManager(client);
        NetworkManager networkManager = OrchestrationTestHelper.CreateNetworkManager(client);
        ImageManager imageManager = OrchestrationTestHelper.CreateImageManager(client);
        ArrkInstance arrk = OrchestrationTestHelper.CreateArrkInstance();
        ResourceInventory inventory = OrchestrationTestHelper.CreateResourceInventory(containerManager, networkManager, arrk.StackName);

        await imageManager.PullImage("nginx:alpine");
        string networkId = await networkManager.CreateOrGetNetwork(arrk.NetworkName);

        string cid1 = await OrchestrationTestHelper.CreateContainerOnNetwork(
            client, "nginx:alpine", $"stackarr-ri-o-{Guid.NewGuid():N}", networkId,
            labels: new()
            {
                [ContainerLabels.Managed] = "true",
                [ContainerLabels.InstanceId] = arrk.StackName,
                [ContainerLabels.Service] = "test-service"
            });
        _containerIds.Add(cid1);

        string cid2 = await OrchestrationTestHelper.CreateContainerOnNetwork(
            client, "nginx:alpine", $"stackarr-ri-u-{Guid.NewGuid():N}", networkId,
            labels: new()
            {
                [ContainerLabels.Managed] = "true",
                [ContainerLabels.InstanceId] = $"other-{arrk.StackName}"
            });
        _containerIds.Add(cid2);

        string cid3 = await OrchestrationTestHelper.CreateContainerOnNetwork(
            client, "nginx:alpine", $"stackarr-ri-n-{Guid.NewGuid():N}", networkId);
        _containerIds.Add(cid3);

        List<ContainerInfo> owned = await inventory.GetOwnedContainers();

        await Assert.That(owned.Count).IsEqualTo(1);
    }

    [Test]
    public async Task GetOwnedContainersByService_FiltersCorrectly()
    {
        using DockerClient client = OrchestrationTestHelper.CreateClient();
        ContainerManager containerManager = OrchestrationTestHelper.CreateContainerManager(client);
        NetworkManager networkManager = OrchestrationTestHelper.CreateNetworkManager(client);
        ImageManager imageManager = OrchestrationTestHelper.CreateImageManager(client);
        ArrkInstance arrk = OrchestrationTestHelper.CreateArrkInstance();
        ResourceInventory inventory = OrchestrationTestHelper.CreateResourceInventory(containerManager, networkManager, arrk.StackName);

        await imageManager.PullImage("nginx:alpine");
        string networkId = await networkManager.CreateOrGetNetwork(arrk.NetworkName);

        string cid1 = await OrchestrationTestHelper.CreateContainerOnNetwork(
            client, "nginx:alpine", $"stackarr-ri-a-{Guid.NewGuid():N}", networkId,
            labels: new()
            {
                [ContainerLabels.Managed] = "true",
                [ContainerLabels.InstanceId] = arrk.StackName,
                [ContainerLabels.Service] = "service-a"
            });
        _containerIds.Add(cid1);

        string cid2 = await OrchestrationTestHelper.CreateContainerOnNetwork(
            client, "nginx:alpine", $"stackarr-ri-b-{Guid.NewGuid():N}", networkId,
            labels: new()
            {
                [ContainerLabels.Managed] = "true",
                [ContainerLabels.InstanceId] = arrk.StackName,
                [ContainerLabels.Service] = "service-b"
            });
        _containerIds.Add(cid2);

        List<ContainerInfo> serviceA = await inventory.GetOwnedContainersByService("service-a");
        await Assert.That(serviceA.Count).IsEqualTo(1);

        List<ContainerInfo> serviceB = await inventory.GetOwnedContainersByService("service-b");
        await Assert.That(serviceB.Count).IsEqualTo(1);
    }

    [Test]
    public async Task IsOwnedContainer_ReturnsTrueForOwned()
    {
        using DockerClient client = OrchestrationTestHelper.CreateClient();
        ContainerManager containerManager = OrchestrationTestHelper.CreateContainerManager(client);
        NetworkManager networkManager = OrchestrationTestHelper.CreateNetworkManager(client);
        ImageManager imageManager = OrchestrationTestHelper.CreateImageManager(client);
        ArrkInstance arrk = OrchestrationTestHelper.CreateArrkInstance();
        ResourceInventory inventory = OrchestrationTestHelper.CreateResourceInventory(containerManager, networkManager, arrk.StackName);

        await imageManager.PullImage("nginx:alpine");
        string networkId = await networkManager.CreateOrGetNetwork(arrk.NetworkName);

        string cid = await OrchestrationTestHelper.CreateContainerOnNetwork(
            client, "nginx:alpine", $"stackarr-ri-io-{Guid.NewGuid():N}", networkId,
            labels: new()
            {
                [ContainerLabels.Managed] = "true",
                [ContainerLabels.InstanceId] = arrk.StackName
            });
        _containerIds.Add(cid);

        bool isOwned = await inventory.IsOwnedContainer(cid);
        await Assert.That(isOwned).IsTrue();
    }

    [Test]
    public async Task GetOwnedContainerCount_ReturnsCorrectCount()
    {
        using DockerClient client = OrchestrationTestHelper.CreateClient();
        ContainerManager containerManager = OrchestrationTestHelper.CreateContainerManager(client);
        NetworkManager networkManager = OrchestrationTestHelper.CreateNetworkManager(client);
        ImageManager imageManager = OrchestrationTestHelper.CreateImageManager(client);
        ArrkInstance arrk = OrchestrationTestHelper.CreateArrkInstance();
        ResourceInventory inventory = OrchestrationTestHelper.CreateResourceInventory(containerManager, networkManager, arrk.StackName);

        await imageManager.PullImage("nginx:alpine");
        string networkId = await networkManager.CreateOrGetNetwork(arrk.NetworkName);

        for (int i = 0; i < 3; i++)
        {
            string cid = await OrchestrationTestHelper.CreateContainerOnNetwork(
                client, "nginx:alpine", $"stackarr-ri-c{i}-{Guid.NewGuid():N}", networkId,
                labels: new()
                {
                    [ContainerLabels.Managed] = "true",
                    [ContainerLabels.InstanceId] = arrk.StackName
                });
            _containerIds.Add(cid);
        }

        int count = await inventory.GetOwnedContainerCount();
        await Assert.That(count).IsEqualTo(3);
    }

    [Test]
    public async Task GetOwnedContainersByServiceCount_GroupsCorrectly()
    {
        using DockerClient client = OrchestrationTestHelper.CreateClient();
        ContainerManager containerManager = OrchestrationTestHelper.CreateContainerManager(client);
        NetworkManager networkManager = OrchestrationTestHelper.CreateNetworkManager(client);
        ImageManager imageManager = OrchestrationTestHelper.CreateImageManager(client);
        ArrkInstance arrk = OrchestrationTestHelper.CreateArrkInstance();
        ResourceInventory inventory = OrchestrationTestHelper.CreateResourceInventory(containerManager, networkManager, arrk.StackName);

        await imageManager.PullImage("nginx:alpine");
        string networkId = await networkManager.CreateOrGetNetwork(arrk.NetworkName);

        string cid1 = await OrchestrationTestHelper.CreateContainerOnNetwork(
            client, "nginx:alpine", $"stackarr-ri-ga1-{Guid.NewGuid():N}", networkId,
            labels: new()
            {
                [ContainerLabels.Managed] = "true",
                [ContainerLabels.InstanceId] = arrk.StackName,
                [ContainerLabels.Service] = "alpha"
            });
        _containerIds.Add(cid1);

        string cid2 = await OrchestrationTestHelper.CreateContainerOnNetwork(
            client, "nginx:alpine", $"stackarr-ri-ga2-{Guid.NewGuid():N}", networkId,
            labels: new()
            {
                [ContainerLabels.Managed] = "true",
                [ContainerLabels.InstanceId] = arrk.StackName,
                [ContainerLabels.Service] = "alpha"
            });
        _containerIds.Add(cid2);

        string cid3 = await OrchestrationTestHelper.CreateContainerOnNetwork(
            client, "nginx:alpine", $"stackarr-ri-gb1-{Guid.NewGuid():N}", networkId,
            labels: new()
            {
                [ContainerLabels.Managed] = "true",
                [ContainerLabels.InstanceId] = arrk.StackName,
                [ContainerLabels.Service] = "beta"
            });
        _containerIds.Add(cid3);

        Dictionary<string, int> counts = await inventory.GetOwnedContainersByServiceCount();
        await Assert.That(counts["alpha"]).IsEqualTo(2);
        await Assert.That(counts["beta"]).IsEqualTo(1);
    }

    [Test]
    public async Task CleanupOrphanedResources_DoesNotTouchOtherInstanceContainers()
    {
        using DockerClient client = OrchestrationTestHelper.CreateClient();
        ContainerManager containerManager = OrchestrationTestHelper.CreateContainerManager(client);
        NetworkManager networkManager = OrchestrationTestHelper.CreateNetworkManager(client);
        ImageManager imageManager = OrchestrationTestHelper.CreateImageManager(client);
        ArrkInstance arrk = OrchestrationTestHelper.CreateArrkInstance();
        ResourceInventory inventory = OrchestrationTestHelper.CreateResourceInventory(containerManager, networkManager, arrk.StackName);

        await imageManager.PullImage("nginx:alpine");
        string networkId = await networkManager.CreateOrGetNetwork(arrk.NetworkName);

        string ownedCid = await OrchestrationTestHelper.CreateContainerOnNetwork(
            client, "nginx:alpine", $"stackarr-ri-co-{Guid.NewGuid():N}", networkId,
            labels: new()
            {
                [ContainerLabels.Managed] = "true",
                [ContainerLabels.InstanceId] = arrk.StackName
            });
        _containerIds.Add(ownedCid);

        string foreignCid = await OrchestrationTestHelper.CreateContainerOnNetwork(
            client, "nginx:alpine", $"stackarr-ri-xo-{Guid.NewGuid():N}", networkId,
            labels: new()
            {
                [ContainerLabels.Managed] = "true",
                [ContainerLabels.InstanceId] = $"test-orphan-{Guid.NewGuid():N}"
            });
        _containerIds.Add(foreignCid);

        await inventory.CleanupOrphanedResources();

        ContainerInfo? ownedStillExists = await containerManager.GetContainer(ownedCid);
        await Assert.That(ownedStillExists).IsNotNull();

        // Containers belonging to another Arrk instance must never be removed.
        ContainerInfo? foreignStillExists = await containerManager.GetContainer(foreignCid);
        await Assert.That(foreignStillExists).IsNotNull();
    }

    [Test]
    public async Task GetOwnedNetworkIds_IncludesManagedNetworks()
    {
        using DockerClient client = OrchestrationTestHelper.CreateClient();
        ContainerManager containerManager = OrchestrationTestHelper.CreateContainerManager(client);
        NetworkManager networkManager = OrchestrationTestHelper.CreateNetworkManager(client);
        ResourceInventory inventory = OrchestrationTestHelper.CreateResourceInventory(containerManager, networkManager);

        string networkName = $"stackarr-own-net-{Guid.NewGuid():N}";
        string networkId = await networkManager.CreateOrGetNetwork(networkName, new()
        {
            [ContainerLabels.Managed] = ContainerLabels.ManagedValue
        });
        _networkIds.Add(networkId);

        List<string> ownedIds = await inventory.GetOwnedNetworkIds();

        await Assert.That(ownedIds).Contains(networkId);
    }

    [Test]
    public async Task GetOwnedNetworkIds_ExcludesUnmanagedNetworks()
    {
        using DockerClient client = OrchestrationTestHelper.CreateClient();
        ContainerManager containerManager = OrchestrationTestHelper.CreateContainerManager(client);
        NetworkManager networkManager = OrchestrationTestHelper.CreateNetworkManager(client);
        ResourceInventory inventory = OrchestrationTestHelper.CreateResourceInventory(containerManager, networkManager);

        string networkName = $"stackarr-unmanaged-net-{Guid.NewGuid():N}";
        string networkId = await networkManager.CreateOrGetNetwork(networkName, labels: null);
        _networkIds.Add(networkId);

        List<string> ownedIds = await inventory.GetOwnedNetworkIds();

        await Assert.That(ownedIds).DoesNotContain(networkId);
    }
}
