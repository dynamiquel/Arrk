using Docker.DotNet;
using Docker.DotNet.Models;
using Arrk.TestSupport;

namespace Arrk.Docker.Tests;

/// <summary>
/// Sanity checks that the Docker daemon the suite relies on is reachable and usable.
/// </summary>
[RequiresDocker]
public class DockerDaemonTests
{
    [Test]
    public async Task Daemon_IsReachable_WithValidSystemInfo()
    {
        using DockerClient client = DockerTestHelper.CreateClient();

        SystemInfoResponse info = await client.System.GetSystemInfoAsync();

        await Assert.That(info.ID).IsNotEmpty();
        await Assert.That(info.ServerVersion).IsNotEmpty();
        await Assert.That(info.Architecture).IsNotEmpty();
        await Assert.That(info.OSType).IsNotEmpty();
    }

    [Test]
    public async Task Daemon_SupportsBridgeNetworks()
    {
        using DockerClient client = DockerTestHelper.CreateClient();

        IList<NetworkResponse> networks = await client.Networks.ListNetworksAsync(new());

        await Assert.That(networks).IsNotEmpty();
        await Assert.That(networks.Any(n => n.Driver == "bridge")).IsTrue();
    }
}
