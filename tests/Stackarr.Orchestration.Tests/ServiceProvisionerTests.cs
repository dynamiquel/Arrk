using Docker.DotNet;
using Stackarr.Core;
using Stackarr.Core.Models;
using Stackarr.Core.Services;
using Stackarr.Docker;
using Stackarr.TestSupport;
using ContainerInspectResponse = Docker.DotNet.Models.ContainerInspectResponse;

namespace Stackarr.Orchestration.Tests;

[NotInParallel("docker")]
[RequiresDocker]
public class ServiceProvisionerTests
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
    public async Task ProvisionContainer_WithNginx_CreatesAndStartsContainer()
    {
        using DockerClient client = OrchestrationTestHelper.CreateClient();
        ContainerManager containerManager = OrchestrationTestHelper.CreateContainerManager(client);
        NetworkManager networkManager = OrchestrationTestHelper.CreateNetworkManager(client);
        ImageManager imageManager = OrchestrationTestHelper.CreateImageManager(client);

        ServiceProvisioner provisioner = OrchestrationTestHelper.CreateServiceProvisioner(containerManager, networkManager, imageManager);

        await imageManager.PullImage("nginx:alpine");

        ContainerSpec spec = new ContainerSpec
        {
            Image = "nginx:alpine",
            Name = "stackarr-test-nginx"
        };

        string containerId = await provisioner.ProvisionContainer(spec);
        _containerIds.Add(containerId);

        ContainerInfo? status = await provisioner.GetContainerStatus("stackarr-test-nginx");
        await Assert.That(status).IsNotNull();
        await Assert.That(status!.State).IsEqualTo(Core.Enums.ContainerState.Running);
    }

    [Test]
    public async Task ProvisionContainer_PublishesAContainerActivity_ThatClearsWhenStarted()
    {
        using DockerClient client = OrchestrationTestHelper.CreateClient();
        ContainerManager containerManager = OrchestrationTestHelper.CreateContainerManager(client);
        NetworkManager networkManager = OrchestrationTestHelper.CreateNetworkManager(client);
        ImageManager imageManager = OrchestrationTestHelper.CreateImageManager(client);

        LiveActivityRegistry liveActivities = new();
        List<LiveActivityEntry> seen = [];
        liveActivities.Changed += () => seen.AddRange(liveActivities.Active);
        ServiceProvisioner provisioner = OrchestrationTestHelper.CreateServiceProvisioner(
            containerManager, networkManager, imageManager, liveActivities: liveActivities);

        await imageManager.PullImage("nginx:alpine");

        ContainerSpec spec = new ContainerSpec
        {
            Image = "nginx:alpine",
            Name = "stackarr-test-live-activity"
        };

        string containerId = await provisioner.ProvisionContainer(spec);
        _containerIds.Add(containerId);

        await Assert.That(seen.Any(e =>
            e.Kind == LiveActivityKind.Container && e.Title.Contains("stackarr-test-live-activity"))).IsTrue();
        await Assert.That(liveActivities.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ProvisionContainer_WithDigestPinnedImage_CreatesAndStartsContainer()
    {
        using DockerClient client = OrchestrationTestHelper.CreateClient();
        ContainerManager containerManager = OrchestrationTestHelper.CreateContainerManager(client);
        NetworkManager networkManager = OrchestrationTestHelper.CreateNetworkManager(client);
        ImageManager imageManager = OrchestrationTestHelper.CreateImageManager(client);

        ServiceProvisioner provisioner = OrchestrationTestHelper.CreateServiceProvisioner(containerManager, networkManager, imageManager);

        string? digest = await imageManager.ResolveDigestAsync("nginx:alpine");
        await Assert.That(digest).IsNotNull();

        ContainerSpec spec = new ContainerSpec
        {
            Image = "nginx:alpine",
            Name = "stackarr-test-digest-pin"
        };

        string containerId = await provisioner.ProvisionContainer(spec);
        _containerIds.Add(containerId);

        ContainerInfo? status = await provisioner.GetContainerStatus("stackarr-test-digest-pin");
        await Assert.That(status).IsNotNull();
        await Assert.That(status!.State).IsEqualTo(Core.Enums.ContainerState.Running);
        await Assert.That(status.Image).Contains("nginx@sha256:");
    }

    [Test]
    public async Task ProvisionContainer_WithLabels_SetsLabelsOnContainer()
    {
        using DockerClient client = OrchestrationTestHelper.CreateClient();
        ContainerManager containerManager = OrchestrationTestHelper.CreateContainerManager(client);
        NetworkManager networkManager = OrchestrationTestHelper.CreateNetworkManager(client);
        ImageManager imageManager = OrchestrationTestHelper.CreateImageManager(client);

        ServiceProvisioner provisioner = OrchestrationTestHelper.CreateServiceProvisioner(containerManager, networkManager, imageManager);

        await imageManager.PullImage("nginx:alpine");

        ContainerSpec spec = new ContainerSpec
        {
            Image = "nginx:alpine",
            Name = "stackarr-test-labels",
            Labels = new() { ["custom.label"] = "test-value" }
        };

        string containerId = await provisioner.ProvisionContainer(spec);
        _containerIds.Add(containerId);

        ContainerInfo? status = await provisioner.GetContainerStatus("stackarr-test-labels");
        await Assert.That(status).IsNotNull();
        await Assert.That(status!.Labels).ContainsKey("custom.label");
        await Assert.That(status.Labels["custom.label"]).IsEqualTo("test-value");
        await Assert.That(status.Labels).ContainsKey(ContainerLabels.Managed);
    }

    [Test]
    public async Task ProvisionContainer_WithVolumes_MountsVolumes()
    {
        using DockerClient client = OrchestrationTestHelper.CreateClient();
        ContainerManager containerManager = OrchestrationTestHelper.CreateContainerManager(client);
        NetworkManager networkManager = OrchestrationTestHelper.CreateNetworkManager(client);
        ImageManager imageManager = OrchestrationTestHelper.CreateImageManager(client);

        ServiceProvisioner provisioner = OrchestrationTestHelper.CreateServiceProvisioner(containerManager, networkManager, imageManager);

        await imageManager.PullImage("nginx:alpine");

        string tempDir = Path.Combine(Path.GetTempPath(), $"stackarr-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            ContainerSpec spec = new ContainerSpec
            {
                Image = "nginx:alpine",
                Name = "stackarr-test-volumes",
                Volumes =
                [
                    new()
                    {
                        HostPath = tempDir,
                        ContainerPath = "/usr/share/nginx/html",
                        Type = VolumeType.Bind,
                        Permissions = VolumePerms.ReadOnly
                    }
                ]
            };

            string containerId = await provisioner.ProvisionContainer(spec);
            _containerIds.Add(containerId);

            ContainerInspectResponse inspect = await containerManager.InspectContainer(containerId);
            var mount = inspect.Mounts.FirstOrDefault(m => m.Destination == "/usr/share/nginx/html");
            await Assert.That(mount).IsNotNull();
            await Assert.That(mount!.Type).IsEqualTo("bind");
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Test]
    public async Task ProvisionContainer_WithRequiredDirectories_CreatesThemBeforeStart()
    {
        using DockerClient client = OrchestrationTestHelper.CreateClient();
        ContainerManager containerManager = OrchestrationTestHelper.CreateContainerManager(client);
        NetworkManager networkManager = OrchestrationTestHelper.CreateNetworkManager(client);
        ImageManager imageManager = OrchestrationTestHelper.CreateImageManager(client);

        ServiceProvisioner provisioner = OrchestrationTestHelper.CreateServiceProvisioner(containerManager, networkManager, imageManager);

        await imageManager.PullImage("nginx:alpine");

        string tempDir = Path.Combine(Path.GetTempPath(), $"stackarr-test-{Guid.NewGuid():N}");
        string required = Path.Combine(tempDir, "sub", "nested");
        try
        {
            ContainerSpec spec = new ContainerSpec
            {
                Image = "nginx:alpine",
                Name = "stackarr-test-requireddirs",
                RequiredDirectories = [required]
            };

            string containerId = await provisioner.ProvisionContainer(spec);
            _containerIds.Add(containerId);

            await Assert.That(Directory.Exists(required)).IsTrue();
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Test]
    public async Task ProvisionContainer_WithPortMapping_BindsPort()
    {
        using DockerClient client = OrchestrationTestHelper.CreateClient();
        ContainerManager containerManager = OrchestrationTestHelper.CreateContainerManager(client);
        NetworkManager networkManager = OrchestrationTestHelper.CreateNetworkManager(client);
        ImageManager imageManager = OrchestrationTestHelper.CreateImageManager(client);

        ServiceProvisioner provisioner = OrchestrationTestHelper.CreateServiceProvisioner(containerManager, networkManager, imageManager);

        await imageManager.PullImage("nginx:alpine");

        ContainerSpec spec = new ContainerSpec
        {
            Image = "nginx:alpine",
            Name = "stackarr-test-ports",
            Ports = [new() { HostPort = 19081, ContainerPort = 80 }]
        };

        string containerId = await provisioner.ProvisionContainer(spec);
        _containerIds.Add(containerId);

        ContainerInspectResponse inspect = await containerManager.InspectContainer(containerId);
        await Assert.That(inspect.HostConfig.PortBindings).IsNotEmpty();
    }

    [Test]
    public async Task ProvisionContainer_WithEnvironment_SetsEnvVars()
    {
        using DockerClient client = OrchestrationTestHelper.CreateClient();
        ContainerManager containerManager = OrchestrationTestHelper.CreateContainerManager(client);
        NetworkManager networkManager = OrchestrationTestHelper.CreateNetworkManager(client);
        ImageManager imageManager = OrchestrationTestHelper.CreateImageManager(client);

        ServiceProvisioner provisioner = OrchestrationTestHelper.CreateServiceProvisioner(containerManager, networkManager, imageManager);

        await imageManager.PullImage("nginx:alpine");

        ContainerSpec spec = new ContainerSpec
        {
            Image = "nginx:alpine",
            Name = "stackarr-test-env",
            Environment = new() { ["MY_VAR"] = "hello" }
        };

        string containerId = await provisioner.ProvisionContainer(spec);
        _containerIds.Add(containerId);

        ContainerInspectResponse inspect = await containerManager.InspectContainer(containerId);
        string? envVar = inspect.Config.Env.FirstOrDefault(e => e.StartsWith("MY_VAR="));
        await Assert.That(envVar).IsNotNull();
        await Assert.That(envVar!).IsEqualTo("MY_VAR=hello");
    }

    [Test]
    public async Task ProvisionContainer_Idempotent_ReturnsSameId()
    {
        using DockerClient client = OrchestrationTestHelper.CreateClient();
        ContainerManager containerManager = OrchestrationTestHelper.CreateContainerManager(client);
        NetworkManager networkManager = OrchestrationTestHelper.CreateNetworkManager(client);
        ImageManager imageManager = OrchestrationTestHelper.CreateImageManager(client);

        ServiceProvisioner provisioner = OrchestrationTestHelper.CreateServiceProvisioner(containerManager, networkManager, imageManager);

        await imageManager.PullImage("nginx:alpine");

        ContainerSpec spec = new ContainerSpec
        {
            Image = "nginx:alpine",
            Name = "stackarr-test-idempotent"
        };

        string id1 = await provisioner.ProvisionContainer(spec);
        _containerIds.Add(id1);
        await OrchestrationTestHelper.WaitForContainerState(containerManager, spec.Name, Core.Enums.ContainerState.Running);
        string id2 = await provisioner.ProvisionContainer(spec);

        await Assert.That(id1).IsEqualTo(id2);

        List<ContainerInfo> containers = await containerManager.ListContainers(labelFilter: $"{ContainerLabels.Managed}={ContainerLabels.ManagedValue}");
        List<ContainerInfo> matching = containers.Where(c => c.Name == "stackarr-test-idempotent").ToList();
        await Assert.That(matching.Count).IsEqualTo(1);
    }

    [Test]
    public async Task ProvisionContainer_OnNetwork_CorrectlyAttachesToNetwork()
    {
        using DockerClient client = OrchestrationTestHelper.CreateClient();
        ContainerManager containerManager = OrchestrationTestHelper.CreateContainerManager(client);
        NetworkManager networkManager = OrchestrationTestHelper.CreateNetworkManager(client);
        ImageManager imageManager = OrchestrationTestHelper.CreateImageManager(client);
        StackarrInstance stackarr = OrchestrationTestHelper.CreateStackarrInstance();

        ServiceProvisioner provisioner = OrchestrationTestHelper.CreateServiceProvisioner(containerManager, networkManager, imageManager);

        await imageManager.PullImage("nginx:alpine");

        ContainerSpec spec = new ContainerSpec
        {
            Image = "nginx:alpine",
            Name = "stackarr-test-network",
            NetworkName = stackarr.NetworkName
        };

        string containerId = await provisioner.ProvisionContainer(spec);
        _containerIds.Add(containerId);

        ContainerInspectResponse inspect = await containerManager.InspectContainer(containerId);
        await Assert.That(inspect.NetworkSettings.Networks.ContainsKey(stackarr.NetworkName)).IsTrue();

        bool exists = await networkManager.NetworkExists(stackarr.NetworkName);
        await Assert.That(exists).IsTrue();
    }

    [Test]
    public async Task ProvisionContainer_WithTraefikLabels_SetsTraefikConfig()
    {
        using DockerClient client = OrchestrationTestHelper.CreateClient();
        ContainerManager containerManager = OrchestrationTestHelper.CreateContainerManager(client);
        NetworkManager networkManager = OrchestrationTestHelper.CreateNetworkManager(client);
        ImageManager imageManager = OrchestrationTestHelper.CreateImageManager(client);

        ServiceProvisioner provisioner = OrchestrationTestHelper.CreateServiceProvisioner(containerManager, networkManager, imageManager);

        await imageManager.PullImage("nginx:alpine");

        ContainerSpec spec = new ContainerSpec
        {
            Image = "nginx:alpine",
            Name = "stackarr-test-traefik",
            Traefik = new()
            {
                ServiceName = "test-service",
                Rule = "PathPrefix(`/test`)",
                Port = 80,
                StripPrefix = "/test"
            }
        };

        string containerId = await provisioner.ProvisionContainer(spec);
        _containerIds.Add(containerId);

        ContainerInfo? status = await provisioner.GetContainerStatus("stackarr-test-traefik");
        await Assert.That(status).IsNotNull();
        await Assert.That(status!.Labels).ContainsKey("traefik.enable");
        await Assert.That(status.Labels["traefik.enable"]).IsEqualTo("true");
        await Assert.That(status.Labels).ContainsKey("traefik.http.routers.test-service.rule");
        await Assert.That(status.Labels["traefik.docker.network"]).IsEqualTo(spec.NetworkName);
        await Assert.That(status.Labels["traefik.http.routers.test-service.middlewares"]).IsEqualTo("test-service-stripprefix");
        await Assert.That(status.Labels["traefik.http.middlewares.test-service-stripprefix.stripprefix.prefixes"]).IsEqualTo("/test");
    }

    [Test]
    public async Task ProvisionContainer_ReplacesStoppedContainer()
    {
        using DockerClient client = OrchestrationTestHelper.CreateClient();
        ContainerManager containerManager = OrchestrationTestHelper.CreateContainerManager(client);
        NetworkManager networkManager = OrchestrationTestHelper.CreateNetworkManager(client);
        ImageManager imageManager = OrchestrationTestHelper.CreateImageManager(client);

        ServiceProvisioner provisioner = OrchestrationTestHelper.CreateServiceProvisioner(containerManager, networkManager, imageManager);

        await imageManager.PullImage("nginx:alpine");

        ContainerSpec spec = new ContainerSpec
        {
            Image = "nginx:alpine",
            Name = "stackarr-test-replace"
        };

        string id1 = await provisioner.ProvisionContainer(spec);
        _containerIds.Add(id1);
        await provisioner.StopContainer("stackarr-test-replace");

        ContainerInfo? stopped = await provisioner.GetContainerStatus("stackarr-test-replace");
        await Assert.That(stopped!.State).IsEqualTo(Core.Enums.ContainerState.Exited);

        string id2 = await provisioner.ProvisionContainer(spec);
        _containerIds.Add(id2);

        ContainerInfo? running = await provisioner.GetContainerStatus("stackarr-test-replace");
        await Assert.That(running!.State).IsEqualTo(Core.Enums.ContainerState.Running);
    }

    [Test]
    public async Task ProvisionContainer_ReusesRunningContainer_WhenLabelsUnchanged()
    {
        using DockerClient client = OrchestrationTestHelper.CreateClient();
        ContainerManager containerManager = OrchestrationTestHelper.CreateContainerManager(client);
        NetworkManager networkManager = OrchestrationTestHelper.CreateNetworkManager(client);
        ImageManager imageManager = OrchestrationTestHelper.CreateImageManager(client);
        ServiceProvisioner provisioner = OrchestrationTestHelper.CreateServiceProvisioner(containerManager, networkManager, imageManager);

        await imageManager.PullImage("nginx:alpine");

        ContainerSpec spec = new ContainerSpec
        {
            Image = "nginx:alpine",
            Name = "stackarr-test-reuse"
        };

        string id1 = await provisioner.ProvisionContainer(spec);
        _containerIds.Add(id1);

        string id2 = await provisioner.ProvisionContainer(spec);
        _containerIds.Add(id2);

        await Assert.That(id2).IsEqualTo(id1);
    }

    [Test]
    public async Task ProvisionContainer_RecreatesRunningContainer_WhenTraefikLabelsChange()
    {
        using DockerClient client = OrchestrationTestHelper.CreateClient();
        ContainerManager containerManager = OrchestrationTestHelper.CreateContainerManager(client);
        NetworkManager networkManager = OrchestrationTestHelper.CreateNetworkManager(client);
        ImageManager imageManager = OrchestrationTestHelper.CreateImageManager(client);
        ServiceProvisioner provisioner = OrchestrationTestHelper.CreateServiceProvisioner(containerManager, networkManager, imageManager);

        await imageManager.PullImage("nginx:alpine");

        ContainerSpec withoutProxy = new ContainerSpec
        {
            Image = "nginx:alpine",
            Name = "stackarr-test-proxy-toggle"
        };

        string id1 = await provisioner.ProvisionContainer(withoutProxy);
        _containerIds.Add(id1);

        ContainerSpec withProxy = new ContainerSpec
        {
            Image = "nginx:alpine",
            Name = "stackarr-test-proxy-toggle",
            Traefik = new()
            {
                ServiceName = "toggled",
                Rule = "PathPrefix(`/toggled`)",
                Port = 80
            }
        };

        string id2 = await provisioner.ProvisionContainer(withProxy);
        _containerIds.Add(id2);

        await Assert.That(id2).IsNotEqualTo(id1);
        ContainerInfo? status = await provisioner.GetContainerStatus("stackarr-test-proxy-toggle");
        await Assert.That(status!.Labels["traefik.enable"]).IsEqualTo("true");
        await Assert.That(status.Labels["traefik.http.routers.toggled.rule"]).IsEqualTo("PathPrefix(`/toggled`)");
    }

    [Test]
    public async Task ProvisionContainer_RecreatesRunningContainer_WhenTraefikLabelsRemoved()
    {
        using DockerClient client = OrchestrationTestHelper.CreateClient();
        ContainerManager containerManager = OrchestrationTestHelper.CreateContainerManager(client);
        NetworkManager networkManager = OrchestrationTestHelper.CreateNetworkManager(client);
        ImageManager imageManager = OrchestrationTestHelper.CreateImageManager(client);
        ServiceProvisioner provisioner = OrchestrationTestHelper.CreateServiceProvisioner(containerManager, networkManager, imageManager);

        await imageManager.PullImage("nginx:alpine");

        ContainerSpec withProxy = new ContainerSpec
        {
            Image = "nginx:alpine",
            Name = "stackarr-test-proxy-remove",
            Traefik = new()
            {
                ServiceName = "removed",
                Rule = "PathPrefix(`/removed`)",
                Port = 80
            }
        };

        string id1 = await provisioner.ProvisionContainer(withProxy);
        _containerIds.Add(id1);

        ContainerSpec withoutProxy = new ContainerSpec
        {
            Image = "nginx:alpine",
            Name = "stackarr-test-proxy-remove"
        };

        string id2 = await provisioner.ProvisionContainer(withoutProxy);
        _containerIds.Add(id2);

        await Assert.That(id2).IsNotEqualTo(id1);
        ContainerInfo? status = await provisioner.GetContainerStatus("stackarr-test-proxy-remove");
        await Assert.That(status!.Labels.ContainsKey("traefik.enable")).IsFalse();
    }

    [Test]
    public async Task StopContainer_RunningContainer_StopsIt()
    {
        using DockerClient client = OrchestrationTestHelper.CreateClient();
        ContainerManager containerManager = OrchestrationTestHelper.CreateContainerManager(client);
        NetworkManager networkManager = OrchestrationTestHelper.CreateNetworkManager(client);
        ImageManager imageManager = OrchestrationTestHelper.CreateImageManager(client);

        ServiceProvisioner provisioner = OrchestrationTestHelper.CreateServiceProvisioner(containerManager, networkManager, imageManager);

        await imageManager.PullImage("nginx:alpine");

        ContainerSpec spec = new ContainerSpec
        {
            Image = "nginx:alpine",
            Name = "stackarr-test-stop"
        };

        string containerId = await provisioner.ProvisionContainer(spec);
        _containerIds.Add(containerId);
        await provisioner.StopContainer("stackarr-test-stop");

        ContainerInfo? status = await provisioner.GetContainerStatus("stackarr-test-stop");
        await Assert.That(status!.State).IsEqualTo(Core.Enums.ContainerState.Exited);
    }

    [Test]
    public async Task EnsureContainerRunning_StoppedContainer_RestartsIt()
    {
        using DockerClient client = OrchestrationTestHelper.CreateClient();
        ContainerManager containerManager = OrchestrationTestHelper.CreateContainerManager(client);
        NetworkManager networkManager = OrchestrationTestHelper.CreateNetworkManager(client);
        ImageManager imageManager = OrchestrationTestHelper.CreateImageManager(client);

        ServiceProvisioner provisioner = OrchestrationTestHelper.CreateServiceProvisioner(containerManager, networkManager, imageManager);

        await imageManager.PullImage("nginx:alpine");

        ContainerSpec spec = new ContainerSpec
        {
            Image = "nginx:alpine",
            Name = "stackarr-test-ensure"
        };

        string containerId = await provisioner.ProvisionContainer(spec);
        _containerIds.Add(containerId);
        await provisioner.StopContainer("stackarr-test-ensure");

        bool result = await provisioner.EnsureContainerRunning("stackarr-test-ensure");
        await Assert.That(result).IsTrue();

        ContainerInfo? status = await provisioner.GetContainerStatus("stackarr-test-ensure");
        await Assert.That(status!.State).IsEqualTo(Core.Enums.ContainerState.Running);
    }

    [Test]
    public async Task GetContainerLogs_ReturnsLogs()
    {
        using DockerClient client = OrchestrationTestHelper.CreateClient();
        ContainerManager containerManager = OrchestrationTestHelper.CreateContainerManager(client);
        NetworkManager networkManager = OrchestrationTestHelper.CreateNetworkManager(client);
        ImageManager imageManager = OrchestrationTestHelper.CreateImageManager(client);

        ServiceProvisioner provisioner = OrchestrationTestHelper.CreateServiceProvisioner(containerManager, networkManager, imageManager);

        await imageManager.PullImage("nginx:alpine");

        ContainerSpec spec = new ContainerSpec
        {
            Image = "nginx:alpine",
            Name = "stackarr-test-logs"
        };

        string containerId = await provisioner.ProvisionContainer(spec);
        _containerIds.Add(containerId);
        await Task.Delay(1000);

        string logs = await provisioner.GetContainerLogs("stackarr-test-logs");
        await Assert.That(logs).IsNotEmpty();
    }
}
