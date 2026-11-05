using Docker.DotNet;
using Docker.DotNet.Models;
using Stackarr.TestSupport;

namespace Stackarr.Docker.Tests;

[NotInParallel("docker")]
[RequiresDocker]
public class NetworkManagerTests
{
    readonly List<string> _networkIds = [];

    [After(Test)]
    public async Task Cleanup()
    {
        using DockerClient client = DockerTestHelper.CreateClient();
        foreach (string id in _networkIds)
        {
            try { await client.Networks.DeleteNetworkAsync(id); } catch { }
        }
        _networkIds.Clear();
    }

    string Name(string suffix) => $"stackarr-net-{suffix}-{Guid.NewGuid():N}"[..40];

    [Test]
    public async Task CreateOrGetNetwork_CreatesBridgeNetwork()
    {
        using DockerClient client = DockerTestHelper.CreateClient();
        NetworkManager networkManager = new(client);
        string name = Name("create");

        string id = await networkManager.CreateOrGetNetwork(name);
        _networkIds.Add(id);

        await Assert.That(id).IsNotEmpty();
        await Assert.That(await networkManager.NetworkExists(name)).IsTrue();

        List<NetworkResponse> networks = await networkManager.ListNetworks();
        await Assert.That(networks.First(n => n.ID == id).Driver).IsEqualTo("bridge");
    }

    [Test]
    public async Task CreateOrGetNetwork_Duplicate_ReturnsExistingId()
    {
        using DockerClient client = DockerTestHelper.CreateClient();
        NetworkManager networkManager = new(client);
        string name = Name("dup");

        string first = await networkManager.CreateOrGetNetwork(name);
        _networkIds.Add(first);
        string second = await networkManager.CreateOrGetNetwork(name);

        await Assert.That(first).IsEqualTo(second);
    }

    [Test]
    public async Task NetworkExists_ReturnsFalseForMissingNetwork()
    {
        using DockerClient client = DockerTestHelper.CreateClient();
        NetworkManager networkManager = new(client);

        await Assert.That(await networkManager.NetworkExists(Name("missing"))).IsFalse();
    }

    [Test]
    public async Task ConnectAndDisconnectContainer_UpdatesNetworkMembership()
    {
        using DockerClient client = DockerTestHelper.CreateClient();
        ImageManager imageManager = new(client);
        NetworkManager networkManager = new(client);
        ContainerManager containerManager = new(client);

        await imageManager.PullImage(DockerTestHelper.TestImage);
        string name = Name("connect");
        string networkId = await networkManager.CreateOrGetNetwork(name);
        _networkIds.Add(networkId);

        string containerId = await DockerTestHelper.CreateContainer(
            DockerTestHelper.TestImage, $"stackarr-net-ctr-{Guid.NewGuid():N}");
        try
        {
            var beforeConnect = await containerManager.InspectContainer(containerId);
            await Assert.That(beforeConnect.NetworkSettings.Networks.ContainsKey(name)).IsFalse();

            await networkManager.ConnectContainer(networkId, containerId);
            var afterConnect = await containerManager.InspectContainer(containerId);
            await Assert.That(afterConnect.NetworkSettings.Networks.ContainsKey(name)).IsTrue();

            await networkManager.DisconnectContainer(networkId, containerId);
            var afterDisconnect = await containerManager.InspectContainer(containerId);
            await Assert.That(afterDisconnect.NetworkSettings.Networks.ContainsKey(name)).IsFalse();
        }
        finally
        {
            try { await containerManager.RemoveContainer(containerId, force: true); } catch { }
        }
    }

    [Test]
    public async Task DeleteNetwork_RemovesIt()
    {
        using DockerClient client = DockerTestHelper.CreateClient();
        NetworkManager networkManager = new(client);
        string name = Name("delete");

        string id = await networkManager.CreateOrGetNetwork(name);
        _networkIds.Add(id);
        await Assert.That(await networkManager.NetworkExists(name)).IsTrue();

        await networkManager.DeleteNetwork(id);

        await Assert.That(await networkManager.NetworkExists(name)).IsFalse();
    }
}
