using System.Net;
using Docker.DotNet;
using Docker.DotNet.Models;
using Arrk.Core.Models;
using Arrk.Docker;
using Arrk.TestSupport;

namespace Arrk.IntegrationTests;

[NotInParallel("docker")]
[RequiresDocker]
public class ServiceDiscoveryTests
{
    readonly List<string> _containerIds = [];
    readonly List<string> _networkIds = [];
    string? _networkId;

    string Name(string suffix) => $"stackarr-int-{TestRun.Id}-{suffix}";

    async Task EnsureNetwork()
    {
        if (_networkId != null) return;
        using DockerClient client = DockerHelper.CreateClient();
        NetworkManager networkManager = new(client);
        _networkId = await networkManager.CreateOrGetNetwork($"stackarr-integration-net-{TestRun.Id}",
            DockerHelper.RunLabels());
        _networkIds.Add(_networkId);
    }

    [After(Test)]
    public async Task Cleanup()
    {
        try
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
        }
        catch { }
        _containerIds.Clear();
        _networkIds.Clear();
        _networkId = null;
    }

    async Task<string> StartService(string image, string name, int hostPort)
    {
        using DockerClient client = DockerHelper.CreateClient();
        ImageManager imageManager = new(client);
        await imageManager.PullImage(image);

        return await DockerHelper.StartContainer(
            image, name, _networkId!,
            new()
            {
                [ContainerLabels.Managed] = ContainerLabels.ManagedValue,
                [ContainerLabels.Service] = name.Replace("stackarr-int-", "")
            },
            hostPort);
    }

    [Test]
    public async Task Docker_IsAvailable()
    {
        bool available = await DockerHelper.IsDockerAvailable();
        await Assert.That(available).IsTrue();
    }

    [Test]
    public async Task Nginx_RespondsToHttp()
    {
        await EnsureNetwork();
        int port = DockerTestContainer.GetAvailablePort();
        string containerId = await StartService("nginx:alpine", Name("nginx"), port);
        _containerIds.Add(containerId);

        bool ready = await DockerHelper.WaitForContainerReady(
            containerId,
            async client =>
            {
                HttpResponseMessage response = await client.GetAsync($"http://localhost:{port}/");
                return response.IsSuccessStatusCode;
            },
            TimeSpan.FromSeconds(30));

        await Assert.That(ready).IsTrue();
    }

    [Test]
    public async Task MultipleContainers_CoexistOnNetwork()
    {
        await EnsureNetwork();
        using DockerClient client = DockerHelper.CreateClient();
        ImageManager imageManager = new(client);
        await imageManager.PullImage("nginx:alpine");

        string cid1 = await DockerHelper.StartContainer(
            "nginx:alpine", Name("multi-1"), _networkId!,
            hostPort: DockerTestContainer.GetAvailablePort());
        string cid2 = await DockerHelper.StartContainer(
            "nginx:alpine", Name("multi-2"), _networkId!,
            hostPort: DockerTestContainer.GetAvailablePort());
        _containerIds.AddRange([cid1, cid2]);

        ContainerManager containerManager = new(client);
        ContainerInspectResponse inspect1 = await containerManager.InspectContainer(cid1);
        ContainerInspectResponse inspect2 = await containerManager.InspectContainer(cid2);

        string? sharedNetwork = inspect1.NetworkSettings.Networks.Keys
            .Intersect(inspect2.NetworkSettings.Networks.Keys)
            .FirstOrDefault();

        await Assert.That(sharedNetwork).IsEqualTo($"stackarr-integration-net-{TestRun.Id}");
    }

    [Test]
    public async Task ContainerLabels_ArePreserved()
    {
        await EnsureNetwork();
        using DockerClient client = DockerHelper.CreateClient();
        ImageManager imageManager = new(client);
        await imageManager.PullImage("nginx:alpine");

        Dictionary<string, string> expectedLabels = new()
        {
            [ContainerLabels.Managed] = ContainerLabels.ManagedValue,
            [ContainerLabels.Service] = "test-service"
        };

        string cid = await DockerHelper.StartContainer(
            "nginx:alpine", Name("labels"), _networkId!,
            expectedLabels, DockerTestContainer.GetAvailablePort());
        _containerIds.Add(cid);

        ContainerManager containerManager = new(client);
        ContainerInspectResponse inspect = await containerManager.InspectContainer(cid);

        foreach (KeyValuePair<string, string> kvp in expectedLabels)
        {
            await Assert.That(inspect.Config.Labels).ContainsKey(kvp.Key);
            await Assert.That(inspect.Config.Labels[kvp.Key]).IsEqualTo(kvp.Value);
        }
    }

    [Test]
    public async Task ContainerLifecycle_InspectStartStopRestart()
    {
        await EnsureNetwork();
        using DockerClient client = DockerHelper.CreateClient();
        ImageManager imageManager = new(client);
        await imageManager.PullImage("nginx:alpine");

        string name = Name("lifecycle");
        string cid = await DockerHelper.StartContainer(
            "nginx:alpine", name, _networkId!,
            hostPort: DockerTestContainer.GetAvailablePort());
        _containerIds.Add(cid);

        ContainerManager containerManager = new(client);

        ContainerInspectResponse inspect = await containerManager.InspectContainer(cid);
        await Assert.That(inspect).IsNotNull();
        await Assert.That(inspect.Name).IsEqualTo($"/{name}");

        await DockerHelper.StopContainer(cid);
        ContainerInfo? stopped = await containerManager.GetContainer(name);
        await Assert.That(stopped!.State).IsEqualTo(Core.Enums.ContainerState.Exited);

        ContainerStartParameters startParams = new();
        await client.Containers.StartContainerAsync(cid, startParams);
        await Task.Delay(1000);
        ContainerInfo? restarted = await containerManager.GetContainer(name);
        await Assert.That(restarted!.State).IsEqualTo(Core.Enums.ContainerState.Running);
    }

    [Test]
    public async Task AppIntegrationTests_HealthEndpoint_ReturnsOk()
    {
        using Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory = new();
        using HttpClient client = factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync("/health");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string content = await response.Content.ReadAsStringAsync();
        await Assert.That(content).Contains("healthy");
    }

    [Test]
    public async Task AppIntegrationTests_Root_ReturnsOk()
    {
        using Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory = new();
        using HttpClient client = factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync("/");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }
}
