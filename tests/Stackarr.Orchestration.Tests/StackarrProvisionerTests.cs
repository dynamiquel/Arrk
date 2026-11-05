using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.EntityFrameworkCore;
using Stackarr.Core;
using Stackarr.Core.Models;
using Stackarr.Docker;
using Stackarr.TestSupport;

namespace Stackarr.Orchestration.Tests;

[NotInParallel("docker")]
[RequiresDocker]
public class StackarrProvisionerTests
{
    // NOTE: test StackarrInstances share the suite-wide network (TestRun.SharedNetworkName), so
    // tests never delete it, it is created on demand and reused across runs.
    [After(Test)]
    public Task Cleanup() => Task.CompletedTask;

    [Test]
    public async Task Provision_CreatesStackarrNetwork()
    {
        using DockerClient client = OrchestrationTestHelper.CreateClient();
        NetworkManager networkManager = OrchestrationTestHelper.CreateNetworkManager(client);
        ImageManager imageManager = OrchestrationTestHelper.CreateImageManager(client);
        StackarrInstance stackarr = OrchestrationTestHelper.CreateStackarrInstance();

        StackarrProvisioner provisioner = OrchestrationTestHelper.CreateStackarrProvisioner(networkManager, imageManager);
        await provisioner.Provision();

        bool exists = await networkManager.NetworkExists(stackarr.NetworkName);
        await Assert.That(exists).IsTrue();
    }

    [Test]
    public async Task Provision_Idempotent_CreatesSameNetwork()
    {
        using DockerClient client = OrchestrationTestHelper.CreateClient();
        NetworkManager networkManager = OrchestrationTestHelper.CreateNetworkManager(client);
        ImageManager imageManager = OrchestrationTestHelper.CreateImageManager(client);
        StackarrInstance stackarr = OrchestrationTestHelper.CreateStackarrInstance();

        StackarrProvisioner provisioner = OrchestrationTestHelper.CreateStackarrProvisioner(networkManager, imageManager);
        await provisioner.Provision();
        string id1 = await networkManager.CreateOrGetNetwork(stackarr.NetworkName);
        string id2 = await networkManager.CreateOrGetNetwork(stackarr.NetworkName);
        await Assert.That(id1).IsEqualTo(id2);
    }

    [Test]
    public async Task Provision_CreatesNetworkWithBridgeDriver()
    {
        using DockerClient client = OrchestrationTestHelper.CreateClient();
        NetworkManager networkManager = OrchestrationTestHelper.CreateNetworkManager(client);
        ImageManager imageManager = OrchestrationTestHelper.CreateImageManager(client);
        StackarrInstance stackarr = OrchestrationTestHelper.CreateStackarrInstance();

        StackarrProvisioner provisioner = OrchestrationTestHelper.CreateStackarrProvisioner(networkManager, imageManager);
        await provisioner.Provision();

        NetworksListParameters listParams = new();
        IList<NetworkResponse>? networks = await client.Networks.ListNetworksAsync(listParams);
        NetworkResponse? stackarrNet = networks.FirstOrDefault(n => n.Name == stackarr.NetworkName);
        await Assert.That(stackarrNet).IsNotNull();
        await Assert.That(stackarrNet!.Driver).IsEqualTo("bridge");
    }

    [Test]
    public async Task Provision_DatabaseContainsExpectedTables()
    {
        using DockerClient client = OrchestrationTestHelper.CreateClient();
        NetworkManager networkManager = OrchestrationTestHelper.CreateNetworkManager(client);
        ImageManager imageManager = OrchestrationTestHelper.CreateImageManager(client);
        IDbContextFactory<StackarrDbContext> factory = OrchestrationTestHelper.CreateInMemoryDbFactory();
        StackarrInstance stackarr = OrchestrationTestHelper.CreateStackarrInstance();

        StackarrProvisioner provisioner = OrchestrationTestHelper.CreateStackarrProvisioner(networkManager, imageManager, factory);
        await provisioner.Provision();

        await using StackarrDbContext db = await factory.CreateDbContextAsync();
        bool canConnect = await db.Database.CanConnectAsync();
        await Assert.That(canConnect).IsTrue();

        int serviceHealthCount = await db.ServiceHealth.CountAsync();
        int notificationCount = await db.Notifications.CountAsync();
        int downloadCount = await db.DownloadStates.CountAsync();
        int statsCount = await db.ContainerStats.CountAsync();

        await Assert.That(serviceHealthCount).IsEqualTo(0);
        await Assert.That(notificationCount).IsEqualTo(0);
        await Assert.That(downloadCount).IsEqualTo(0);
        await Assert.That(statsCount).IsEqualTo(0);

        bool exists = await networkManager.NetworkExists(stackarr.NetworkName);
        await Assert.That(exists).IsTrue();
    }
}
