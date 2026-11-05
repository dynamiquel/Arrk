using Docker.DotNet;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Stackarr.Configuration;
using Stackarr.Core.Enums;
using Stackarr.Core.Services;
using Stackarr.Docker;
using Stackarr.TestSupport;

using Stackarr.Core.Configuration;

namespace Stackarr.Orchestration.Tests;

[NotInParallel("docker")]
[RequiresDocker]
public class ServiceStateManagerTests
{
    static IConfiguration CreateConfiguration(
        bool tvEnabled = true,
        bool musicEnabled = true,
        bool jellyfinEnabled = true,
        bool navidromeEnabled = true,
        bool vpnEnabled = false)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["services:tv:enabled"] = tvEnabled.ToString().ToLower(),
                ["services:music:enabled"] = musicEnabled.ToString().ToLower(),
                ["services:jellyfin:enabled"] = jellyfinEnabled.ToString().ToLower(),
                ["services:navidrome:enabled"] = navidromeEnabled.ToString().ToLower(),
                ["vpn:enabled"] = vpnEnabled.ToString().ToLower()
            })
            .Build();
    }

    [Test]
    public async Task GetAllServices_WithNoContainers_ReturnsAllServices()
    {
        using DockerClient client = OrchestrationTestHelper.CreateClient();
        ContainerManager containerManager = OrchestrationTestHelper.CreateContainerManager(client);
        NetworkManager networkManager = OrchestrationTestHelper.CreateNetworkManager(client);
        string instanceId = $"sm-empty-{Guid.NewGuid():N}";
        ResourceInventory inventory = OrchestrationTestHelper.CreateResourceInventory(containerManager, networkManager, instanceId);
        ServiceRegistry registry = TestHelper.CreateDefault();
        DockerMetricsCollector metricsCollector = new DockerMetricsCollector(client, containerManager, networkManager, registry);
        IConfiguration configuration = CreateConfiguration();
        MutableOptionsMonitor<StackarrConfig> configMonitor = new MutableOptionsMonitor<StackarrConfig>(ConfigDefaults.GetDefaults());
        ILogger<ServiceStateManager> logger = OrchestrationTestHelper.CreateLogger<ServiceStateManager>();

        ServiceStateManager stateManager = new ServiceStateManager(inventory, registry, metricsCollector, configMonitor, configuration, logger);

        List<Service> services = await stateManager.GetAllServices();

        await Assert.That(services).IsNotEmpty();
        foreach (Service svc in services)
        {
            await Assert.That(svc.State.Containers).IsEmpty();
            await Assert.That(svc.State.AggregateState).IsEqualTo(ContainerState.Unknown);
            await Assert.That(svc).IsNotNull();
        }
    }

    [Test]
    public async Task GetAllServices_EnabledServicesMarkedEnabled()
    {
        using DockerClient client = OrchestrationTestHelper.CreateClient();
        ContainerManager containerManager = OrchestrationTestHelper.CreateContainerManager(client);
        NetworkManager networkManager = OrchestrationTestHelper.CreateNetworkManager(client);
        string instanceId = $"sm-enabled-{Guid.NewGuid():N}";
        ResourceInventory inventory = OrchestrationTestHelper.CreateResourceInventory(containerManager, networkManager, instanceId);
        ServiceRegistry registry = TestHelper.CreateDefault();
        DockerMetricsCollector metricsCollector = new DockerMetricsCollector(client, containerManager, networkManager, registry);
        IConfiguration configuration = CreateConfiguration();
        MutableOptionsMonitor<StackarrConfig> configMonitor = new MutableOptionsMonitor<StackarrConfig>(ConfigDefaults.GetDefaults());
        ILogger<ServiceStateManager> logger = OrchestrationTestHelper.CreateLogger<ServiceStateManager>();

        ServiceStateManager stateManager = new ServiceStateManager(inventory, registry, metricsCollector, configMonitor, configuration, logger);

        List<Service> services = await stateManager.GetAllServices();

        Service? jellyfin = services.FirstOrDefault(s => s.Definition.Key == "jellyfin");
        await Assert.That(jellyfin).IsNotNull();
        await Assert.That(jellyfin!.State.IsEnabled).IsTrue();
    }

    [Test]
    public async Task GetAllServices_DisabledServicesMarkedDisabled()
    {
        using DockerClient client = OrchestrationTestHelper.CreateClient();
        ContainerManager containerManager = OrchestrationTestHelper.CreateContainerManager(client);
        NetworkManager networkManager = OrchestrationTestHelper.CreateNetworkManager(client);
        string instanceId = $"sm-disabled-{Guid.NewGuid():N}";
        ResourceInventory inventory = OrchestrationTestHelper.CreateResourceInventory(containerManager, networkManager, instanceId);
        ServiceRegistry registry = TestHelper.CreateDefault();
        DockerMetricsCollector metricsCollector = new DockerMetricsCollector(client, containerManager, networkManager, registry);
        IConfiguration configuration = CreateConfiguration(jellyfinEnabled: false, tvEnabled: false);
        MutableOptionsMonitor<StackarrConfig> configMonitor = new MutableOptionsMonitor<StackarrConfig>(new());
        ILogger<ServiceStateManager> logger = OrchestrationTestHelper.CreateLogger<ServiceStateManager>();

        ServiceStateManager stateManager = new ServiceStateManager(inventory, registry, metricsCollector, configMonitor, configuration, logger);

        List<Service> services = await stateManager.GetAllServices();

        Service? jellyfin = services.FirstOrDefault(s => s.Definition.Key == "jellyfin");
        await Assert.That(jellyfin).IsNotNull();
        await Assert.That(jellyfin.State.IsEnabled).IsFalse();

        Service? tv = services.FirstOrDefault(s => s.Definition.Key == "tv");
        await Assert.That(tv).IsNotNull();
        await Assert.That(tv.State.IsEnabled).IsFalse();
    }

    [Test]
    public async Task GetAllServices_InfrastructureServicesAlwaysEnabled()
    {
        using DockerClient client = OrchestrationTestHelper.CreateClient();
        ContainerManager containerManager = OrchestrationTestHelper.CreateContainerManager(client);
        NetworkManager networkManager = OrchestrationTestHelper.CreateNetworkManager(client);
        string instanceId = $"sm-infra-{Guid.NewGuid():N}";
        ResourceInventory inventory = OrchestrationTestHelper.CreateResourceInventory(containerManager, networkManager, instanceId);
        ServiceRegistry registry = TestHelper.CreateDefault();
        DockerMetricsCollector metricsCollector = new DockerMetricsCollector(client, containerManager, networkManager, registry);
        IConfiguration configuration = CreateConfiguration(jellyfinEnabled: false, tvEnabled: false);
        MutableOptionsMonitor<StackarrConfig> configMonitor = new MutableOptionsMonitor<StackarrConfig>(new());
        ILogger<ServiceStateManager> logger = OrchestrationTestHelper.CreateLogger<ServiceStateManager>();

        ServiceStateManager stateManager = new ServiceStateManager(inventory, registry, metricsCollector, configMonitor, configuration, logger);

        List<Service> services = await stateManager.GetAllServices();

        List<Service> infraServices = services.Where(s => s.Definition.IsInfrastructure).ToList();
        foreach (Service svc in infraServices)
        {
            await Assert.That(svc.State.IsEnabled).IsTrue();
        }
    }

    [Test]
    public async Task GetService_ReturnsCorrectService()
    {
        using DockerClient client = OrchestrationTestHelper.CreateClient();
        ContainerManager containerManager = OrchestrationTestHelper.CreateContainerManager(client);
        NetworkManager networkManager = OrchestrationTestHelper.CreateNetworkManager(client);
        string instanceId = $"sm-get-{Guid.NewGuid():N}";
        ResourceInventory inventory = OrchestrationTestHelper.CreateResourceInventory(containerManager, networkManager, instanceId);
        ServiceRegistry registry = TestHelper.CreateDefault();
        DockerMetricsCollector metricsCollector = new DockerMetricsCollector(client, containerManager, networkManager, registry);
        IConfiguration configuration = CreateConfiguration();
        MutableOptionsMonitor<StackarrConfig> configMonitor = new MutableOptionsMonitor<StackarrConfig>(ConfigDefaults.GetDefaults());
        ILogger<ServiceStateManager> logger = OrchestrationTestHelper.CreateLogger<ServiceStateManager>();

        ServiceStateManager stateManager = new ServiceStateManager(inventory, registry, metricsCollector, configMonitor, configuration, logger);

        Service? svc = await stateManager.GetService("jellyfin");

        await Assert.That(svc).IsNotNull();
        await Assert.That(svc!.Definition.Key).IsEqualTo("jellyfin");
        await Assert.That(svc.State).IsNotNull();
    }

    [Test]
    public async Task GetService_ReturnsNullForUnknownKey()
    {
        using DockerClient client = OrchestrationTestHelper.CreateClient();
        ContainerManager containerManager = OrchestrationTestHelper.CreateContainerManager(client);
        NetworkManager networkManager = OrchestrationTestHelper.CreateNetworkManager(client);
        string instanceId = $"sm-unknown-{Guid.NewGuid():N}";
        ResourceInventory inventory = OrchestrationTestHelper.CreateResourceInventory(containerManager, networkManager, instanceId);
        ServiceRegistry registry = TestHelper.CreateDefault();
        DockerMetricsCollector metricsCollector = new DockerMetricsCollector(client, containerManager, networkManager, registry);
        IConfiguration configuration = CreateConfiguration();
        MutableOptionsMonitor<StackarrConfig> configMonitor = new MutableOptionsMonitor<StackarrConfig>(ConfigDefaults.GetDefaults());
        ILogger<ServiceStateManager> logger = OrchestrationTestHelper.CreateLogger<ServiceStateManager>();

        ServiceStateManager stateManager = new ServiceStateManager(inventory, registry, metricsCollector, configMonitor, configuration, logger);

        Service? svc = await stateManager.GetService("nonexistent");

        await Assert.That(svc).IsNull();
    }

}
