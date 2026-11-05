using Docker.DotNet;
using Docker.DotNet.Models;
using Stackarr.Core.Models;
using Stackarr.Docker;
using Stackarr.TestSupport;

namespace Stackarr.Orchestration.Tests;

/// <summary>
/// Verifies containers placed on a Stackarr network actually share it and can be attached
/// after creation. Overlaps with <see cref="NetworkIsolationTests"/> only in that the latter
/// proves isolation; this proves co-location and late attachment.
/// </summary>
[NotInParallel("docker")]
[RequiresDocker]
public class ServiceCommunicationTests
{
    readonly List<string> _containerIds = [];
    readonly List<string> _networkIds = [];
    readonly string _uid = Guid.NewGuid().ToString("N")[..8];

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

    string Name(string suffix) => $"stackarr-comm-{suffix}-{_uid}";

    [Test]
    public async Task ContainersOnSameNetwork_ShareItWithDistinctIps()
    {
        using DockerClient client = OrchestrationTestHelper.CreateClient();
        ImageManager imageManager = OrchestrationTestHelper.CreateImageManager(client);
        NetworkManager networkManager = OrchestrationTestHelper.CreateNetworkManager(client);

        await imageManager.PullImage("nginx:alpine");
        string networkName = Name("share");
        string networkId = await networkManager.CreateOrGetNetwork(networkName);
        _networkIds.Add(networkId);

        string cid1 = await OrchestrationTestHelper.CreateContainerOnNetwork(
            client, "nginx:alpine", Name("share-1"), networkId);
        string cid2 = await OrchestrationTestHelper.CreateContainerOnNetwork(
            client, "nginx:alpine", Name("share-2"), networkId);
        _containerIds.AddRange([cid1, cid2]);

        ContainerManager containerManager = OrchestrationTestHelper.CreateContainerManager(client);
        ContainerInspectResponse inspect1 = await containerManager.InspectContainer(cid1);
        ContainerInspectResponse inspect2 = await containerManager.InspectContainer(cid2);

        EndpointSettings endpoint1 = inspect1.NetworkSettings.Networks[networkName];
        EndpointSettings endpoint2 = inspect2.NetworkSettings.Networks[networkName];

        await Assert.That(endpoint1.IPAddress).IsNotEmpty();
        await Assert.That(endpoint2.IPAddress).IsNotEmpty();
        await Assert.That(endpoint1.IPAddress).IsNotEqualTo(endpoint2.IPAddress);
    }

    [Test]
    public async Task ContainerAddedToNetwork_AfterCreation_CanCommunicate()
    {
        using DockerClient client = OrchestrationTestHelper.CreateClient();
        ImageManager imageManager = OrchestrationTestHelper.CreateImageManager(client);
        NetworkManager networkManager = OrchestrationTestHelper.CreateNetworkManager(client);

        await imageManager.PullImage("nginx:alpine");
        string networkName = Name("late");
        string networkId = await networkManager.CreateOrGetNetwork(networkName);
        _networkIds.Add(networkId);

        string cid1 = await OrchestrationTestHelper.CreateContainerOnNetwork(
            client, "nginx:alpine", Name("late-1"), networkId);
        _containerIds.Add(cid1);

        CreateContainerParameters parameters = new()
        {
            Image = "nginx:alpine",
            Name = Name("late-2")
        };
        CreateContainerResponse? response = await client.Containers.CreateContainerAsync(parameters);
        _containerIds.Add(response.ID);
        await client.Containers.StartContainerAsync(response.ID, new ContainerStartParameters());

        ContainerManager containerManager = OrchestrationTestHelper.CreateContainerManager(client);
        ContainerInspectResponse beforeConnect = await containerManager.InspectContainer(response.ID);
        await Assert.That(beforeConnect.NetworkSettings.Networks.ContainsKey(networkName)).IsFalse();

        await networkManager.ConnectContainer(networkId, response.ID);

        ContainerInspectResponse afterConnect = await containerManager.InspectContainer(response.ID);
        await Assert.That(afterConnect.NetworkSettings.Networks.ContainsKey(networkName)).IsTrue();
    }
}
