using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.EntityFrameworkCore;
using Arrk.Core;
using Arrk.Core.Models;
using Arrk.Docker;
using Arrk.TestSupport;

namespace Arrk.Orchestration.Tests;

[NotInParallel("docker")]
[RequiresDocker]
public class ArrkProvisionerTests
{
    // NOTE: test ArrkInstances share the suite-wide network (TestRun.SharedNetworkName), so
    // tests never delete it, it is created on demand and reused across runs.
    [After(Test)]
    public Task Cleanup() => Task.CompletedTask;

    [Test]
    public async Task Provision_CreatesArrkNetwork()
    {
        using DockerClient client = OrchestrationTestHelper.CreateClient();
        NetworkManager networkManager = OrchestrationTestHelper.CreateNetworkManager(client);
        ImageManager imageManager = OrchestrationTestHelper.CreateImageManager(client);
        ArrkInstance arrk = OrchestrationTestHelper.CreateArrkInstance();

        ArrkProvisioner provisioner = OrchestrationTestHelper.CreateArrkProvisioner(networkManager, imageManager);
        await provisioner.Provision();

        bool exists = await networkManager.NetworkExists(arrk.NetworkName);
        await Assert.That(exists).IsTrue();
    }

    [Test]
    public async Task Provision_Idempotent_CreatesSameNetwork()
    {
        using DockerClient client = OrchestrationTestHelper.CreateClient();
        NetworkManager networkManager = OrchestrationTestHelper.CreateNetworkManager(client);
        ImageManager imageManager = OrchestrationTestHelper.CreateImageManager(client);
        ArrkInstance arrk = OrchestrationTestHelper.CreateArrkInstance();

        ArrkProvisioner provisioner = OrchestrationTestHelper.CreateArrkProvisioner(networkManager, imageManager);
        await provisioner.Provision();
        string id1 = await networkManager.CreateOrGetNetwork(arrk.NetworkName);
        string id2 = await networkManager.CreateOrGetNetwork(arrk.NetworkName);
        await Assert.That(id1).IsEqualTo(id2);
    }

    [Test]
    public async Task Provision_CreatesNetworkWithBridgeDriver()
    {
        using DockerClient client = OrchestrationTestHelper.CreateClient();
        NetworkManager networkManager = OrchestrationTestHelper.CreateNetworkManager(client);
        ImageManager imageManager = OrchestrationTestHelper.CreateImageManager(client);
        ArrkInstance arrk = OrchestrationTestHelper.CreateArrkInstance();

        ArrkProvisioner provisioner = OrchestrationTestHelper.CreateArrkProvisioner(networkManager, imageManager);
        await provisioner.Provision();

        NetworksListParameters listParams = new();
        IList<NetworkResponse>? networks = await client.Networks.ListNetworksAsync(listParams);
        NetworkResponse? arrkNet = networks.FirstOrDefault(n => n.Name == arrk.NetworkName);
        await Assert.That(arrkNet).IsNotNull();
        await Assert.That(arrkNet!.Driver).IsEqualTo("bridge");
    }

    [Test]
    public async Task Provision_DatabaseContainsExpectedTables()
    {
        using DockerClient client = OrchestrationTestHelper.CreateClient();
        NetworkManager networkManager = OrchestrationTestHelper.CreateNetworkManager(client);
        ImageManager imageManager = OrchestrationTestHelper.CreateImageManager(client);
        IDbContextFactory<ArrkDbContext> factory = OrchestrationTestHelper.CreateInMemoryDbFactory();
        ArrkInstance arrk = OrchestrationTestHelper.CreateArrkInstance();

        ArrkProvisioner provisioner = OrchestrationTestHelper.CreateArrkProvisioner(networkManager, imageManager, factory);
        await provisioner.Provision();

        await using ArrkDbContext db = await factory.CreateDbContextAsync();
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

        bool exists = await networkManager.NetworkExists(arrk.NetworkName);
        await Assert.That(exists).IsTrue();
    }
}
