using Docker.DotNet;
using Microsoft.Extensions.Configuration;
using Arrk.Configuration;
using Arrk.Core.Models;
using Arrk.Core.Services;
using Arrk.Docker;
using Arrk.TestSupport;

using Arrk.Core.Configuration;

namespace Arrk.Orchestration.Tests;

[NotInParallel("docker")]
public class DashboardServiceTests
{
    static IConfiguration CreateConfiguration() => new ConfigurationBuilder().Build();

    [Test]
    public async Task GetDashboardSummary_IncludesHostMetrics()
    {
        using DockerClient client = OrchestrationTestHelper.CreateClient();
        ContainerManager containerManager = OrchestrationTestHelper.CreateContainerManager(client);
        NetworkManager networkManager = OrchestrationTestHelper.CreateNetworkManager(client);
        string instanceId = $"dash-host-{Guid.NewGuid():N}";
        ResourceInventory inventory = OrchestrationTestHelper.CreateResourceInventory(containerManager, networkManager, instanceId);
        ServiceRegistry registry = TestHelper.CreateDefault();
        DockerMetricsCollector metricsCollector = new DockerMetricsCollector(client, containerManager, networkManager, registry);
        IConfiguration configuration = CreateConfiguration();
        MutableOptionsMonitor<ArrkConfig> configMonitor = new MutableOptionsMonitor<ArrkConfig>(ConfigDefaults.GetDefaults());

        ServiceStateManager stateManager = new ServiceStateManager(inventory, registry, metricsCollector, configMonitor, configuration, OrchestrationTestHelper.CreateLogger<ServiceStateManager>());
        HostMetricsService hostMetricsService = new HostMetricsService();
        DashboardService dashboardService = new DashboardService(stateManager, hostMetricsService, metricsCollector, OrchestrationTestHelper.CreateLogger<DashboardService>());

        DashboardSummary summary = await dashboardService.GetDashboardSummary();

        await Assert.That(summary.HostMetrics).IsNotNull();
        await Assert.That(summary.HostMetrics!.MemoryTotalBytes).IsGreaterThan(0);
    }

    [Test]
    public async Task GetDashboardSummary_IncludesDockerMetrics()
    {
        using DockerClient client = OrchestrationTestHelper.CreateClient();
        ContainerManager containerManager = OrchestrationTestHelper.CreateContainerManager(client);
        NetworkManager networkManager = OrchestrationTestHelper.CreateNetworkManager(client);
        string instanceId = $"dash-docker-{Guid.NewGuid():N}";
        ResourceInventory inventory = OrchestrationTestHelper.CreateResourceInventory(containerManager, networkManager, instanceId);
        ServiceRegistry registry = TestHelper.CreateDefault();
        DockerMetricsCollector metricsCollector = new DockerMetricsCollector(client, containerManager, networkManager, registry);
        IConfiguration configuration = CreateConfiguration();
        MutableOptionsMonitor<ArrkConfig> configMonitor = new MutableOptionsMonitor<ArrkConfig>(ConfigDefaults.GetDefaults());

        ServiceStateManager stateManager = new ServiceStateManager(inventory, registry, metricsCollector, configMonitor, configuration, OrchestrationTestHelper.CreateLogger<ServiceStateManager>());
        HostMetricsService hostMetricsService = new HostMetricsService();
        DashboardService dashboardService = new DashboardService(stateManager, hostMetricsService, metricsCollector, OrchestrationTestHelper.CreateLogger<DashboardService>());

        DashboardSummary summary = await dashboardService.GetDashboardSummary();

        await Assert.That(summary.DockerMetrics).IsNotNull();
        await Assert.That(summary.DockerMetrics.TotalContainers).IsGreaterThanOrEqualTo(0);
    }

    [Test]
    public async Task GetDashboardSummary_CountsMatchServiceStates()
    {
        using DockerClient client = OrchestrationTestHelper.CreateClient();
        ContainerManager containerManager = OrchestrationTestHelper.CreateContainerManager(client);
        NetworkManager networkManager = OrchestrationTestHelper.CreateNetworkManager(client);
        string instanceId = $"dash-counts-{Guid.NewGuid():N}";
        ResourceInventory inventory = OrchestrationTestHelper.CreateResourceInventory(containerManager, networkManager, instanceId);
        ServiceRegistry registry = TestHelper.CreateDefault();
        DockerMetricsCollector metricsCollector = new DockerMetricsCollector(client, containerManager, networkManager, registry);
        IConfiguration configuration = CreateConfiguration();
        MutableOptionsMonitor<ArrkConfig> configMonitor = new MutableOptionsMonitor<ArrkConfig>(ConfigDefaults.GetDefaults());

        ServiceStateManager stateManager = new ServiceStateManager(inventory, registry, metricsCollector, configMonitor, configuration, OrchestrationTestHelper.CreateLogger<ServiceStateManager>());
        HostMetricsService hostMetricsService = new HostMetricsService();
        DashboardService dashboardService = new DashboardService(stateManager, hostMetricsService, metricsCollector, OrchestrationTestHelper.CreateLogger<DashboardService>());

        DashboardSummary summary = await dashboardService.GetDashboardSummary();

        int expectedHealthy = summary.Services.Count(s => s.State.IsHealthy);
        int expectedUnhealthy = summary.Services.Count(s => s.State.IsEnabled && !s.State.IsHealthy);

        await Assert.That(summary.DockerMetrics.HealthyContainers).IsEqualTo(expectedHealthy);
        await Assert.That(summary.DockerMetrics.UnhealthyContainers).IsEqualTo(expectedUnhealthy);
    }

}
