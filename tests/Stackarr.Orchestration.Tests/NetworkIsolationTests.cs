using Docker.DotNet;
using Docker.DotNet.Models;
using Stackarr.Core;
using Stackarr.Core.Models;
using Stackarr.Docker;
using Stackarr.TestSupport;

namespace Stackarr.Orchestration.Tests;

[NotInParallel("docker")]
[RequiresDocker]
public class NetworkIsolationTests
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

    string Name(string suffix) => $"stackarr-iso-{suffix}-{_uid}";

    [Test]
    public async Task ContainersOnDifferentNetworks_DoNotShareNetworks()
    {
        using DockerClient client = OrchestrationTestHelper.CreateClient();
        ImageManager imageManager = OrchestrationTestHelper.CreateImageManager(client);
        NetworkManager networkManager = OrchestrationTestHelper.CreateNetworkManager(client);

        await imageManager.PullImage("nginx:alpine");

        string net1 = await networkManager.CreateOrGetNetwork(Name("diff-1"));
        string net2 = await networkManager.CreateOrGetNetwork(Name("diff-2"));
        _networkIds.AddRange([net1, net2]);

        string cid1 = await OrchestrationTestHelper.CreateContainerOnNetwork(
            client, "nginx:alpine", Name("diff-c1"), net1, hostPort: 11910);
        string cid2 = await OrchestrationTestHelper.CreateContainerOnNetwork(
            client, "nginx:alpine", Name("diff-c2"), net2, hostPort: 11911);
        _containerIds.AddRange([cid1, cid2]);

        ContainerManager containerManager = OrchestrationTestHelper.CreateContainerManager(client);
        ContainerInspectResponse inspect1 = await containerManager.InspectContainer(cid1);
        ContainerInspectResponse inspect2 = await containerManager.InspectContainer(cid2);

        List<string> nets1 = inspect1.NetworkSettings.Networks.Keys.ToList();
        List<string> nets2 = inspect2.NetworkSettings.Networks.Keys.ToList();

        List<string> shared = nets1.Intersect(nets2).ToList();
        await Assert.That(shared).IsEmpty();
    }

    [Test]
    public async Task ThreeContainers_ThreeNetworks_NoOverlap()
    {
        using DockerClient client = OrchestrationTestHelper.CreateClient();
        ImageManager imageManager = OrchestrationTestHelper.CreateImageManager(client);
        NetworkManager networkManager = OrchestrationTestHelper.CreateNetworkManager(client);

        await imageManager.PullImage("nginx:alpine");

        string net1 = await networkManager.CreateOrGetNetwork(Name("three-1"));
        string net2 = await networkManager.CreateOrGetNetwork(Name("three-2"));
        string net3 = await networkManager.CreateOrGetNetwork(Name("three-3"));
        _networkIds.AddRange([net1, net2, net3]);

        string cid1 = await OrchestrationTestHelper.CreateContainerOnNetwork(
            client, "nginx:alpine", Name("three-c1"), net1);
        string cid2 = await OrchestrationTestHelper.CreateContainerOnNetwork(
            client, "nginx:alpine", Name("three-c2"), net2);
        string cid3 = await OrchestrationTestHelper.CreateContainerOnNetwork(
            client, "nginx:alpine", Name("three-c3"), net3);
        _containerIds.AddRange([cid1, cid2, cid3]);

        ContainerManager containerManager = OrchestrationTestHelper.CreateContainerManager(client);
        ContainerInspectResponse inspect1 = await containerManager.InspectContainer(cid1);
        ContainerInspectResponse inspect2 = await containerManager.InspectContainer(cid2);
        ContainerInspectResponse inspect3 = await containerManager.InspectContainer(cid3);

        List<string> nets1 = inspect1.NetworkSettings.Networks.Keys.ToList();
        List<string> nets2 = inspect2.NetworkSettings.Networks.Keys.ToList();
        List<string> nets3 = inspect3.NetworkSettings.Networks.Keys.ToList();

        await Assert.That(nets1.Intersect(nets2).ToList()).IsEmpty();
        await Assert.That(nets2.Intersect(nets3).ToList()).IsEmpty();
        await Assert.That(nets1.Intersect(nets3).ToList()).IsEmpty();
    }

    [Test]
    public async Task ContainerOnOneNetwork_CannotSeeContainerOnOther()
    {
        using DockerClient client = OrchestrationTestHelper.CreateClient();
        ImageManager imageManager = OrchestrationTestHelper.CreateImageManager(client);
        NetworkManager networkManager = OrchestrationTestHelper.CreateNetworkManager(client);

        await imageManager.PullImage("nginx:alpine");

        string net1Name = Name("isolated-1");
        string net2Name = Name("isolated-2");
        string net1 = await networkManager.CreateOrGetNetwork(net1Name);
        string net2 = await networkManager.CreateOrGetNetwork(net2Name);
        _networkIds.AddRange([net1, net2]);

        string cid1 = await OrchestrationTestHelper.CreateContainerOnNetwork(
            client, "nginx:alpine", Name("isolated-c1"), net1, hostPort: 11920);
        string cid2 = await OrchestrationTestHelper.CreateContainerOnNetwork(
            client, "nginx:alpine", Name("isolated-c2"), net2, hostPort: 11921);
        _containerIds.AddRange([cid1, cid2]);

        ContainerManager containerManager = OrchestrationTestHelper.CreateContainerManager(client);
        ContainerInspectResponse inspect1 = await containerManager.InspectContainer(cid1);
        ContainerInspectResponse inspect2 = await containerManager.InspectContainer(cid2);

        await Assert.That(inspect1.NetworkSettings.Networks.ContainsKey(net1Name)).IsTrue();
        await Assert.That(inspect1.NetworkSettings.Networks.ContainsKey(net2Name)).IsFalse();
        await Assert.That(inspect2.NetworkSettings.Networks.ContainsKey(net2Name)).IsTrue();
        await Assert.That(inspect2.NetworkSettings.Networks.ContainsKey(net1Name)).IsFalse();
    }

    [Test]
    public async Task StackarrNet_IsIsolatedFromTestNets()
    {
        using DockerClient client = OrchestrationTestHelper.CreateClient();
        ImageManager imageManager = OrchestrationTestHelper.CreateImageManager(client);
        NetworkManager networkManager = OrchestrationTestHelper.CreateNetworkManager(client);
        StackarrInstance stackarr = OrchestrationTestHelper.CreateStackarrInstance();

        await imageManager.PullImage("nginx:alpine");

        // The shared test network is never deleted; the isolated test nets are cleaned per test.
        string stackarrNet = await networkManager.CreateOrGetNetwork(stackarr.NetworkName);
        string testNetName = Name("vs-stackarr");
        string testNet = await networkManager.CreateOrGetNetwork(testNetName);
        _networkIds.Add(testNet);

        string cid1 = await OrchestrationTestHelper.CreateContainerOnNetwork(
            client, "nginx:alpine", Name("on-stackarr"), stackarrNet);
        string cid2 = await OrchestrationTestHelper.CreateContainerOnNetwork(
            client, "nginx:alpine", Name("on-test"), testNet);
        _containerIds.AddRange([cid1, cid2]);

        ContainerManager containerManager = OrchestrationTestHelper.CreateContainerManager(client);
        ContainerInspectResponse inspect1 = await containerManager.InspectContainer(cid1);
        ContainerInspectResponse inspect2 = await containerManager.InspectContainer(cid2);

        await Assert.That(inspect1.NetworkSettings.Networks.ContainsKey(stackarr.NetworkName)).IsTrue();
        await Assert.That(inspect1.NetworkSettings.Networks.ContainsKey(testNetName)).IsFalse();
        await Assert.That(inspect2.NetworkSettings.Networks.ContainsKey(testNetName)).IsTrue();
        await Assert.That(inspect2.NetworkSettings.Networks.ContainsKey(stackarr.NetworkName)).IsFalse();
    }
}
