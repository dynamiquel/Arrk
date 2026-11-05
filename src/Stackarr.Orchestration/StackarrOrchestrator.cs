using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stackarr.Core;
using Stackarr.Core.Configuration;
using Stackarr.Core.Enums;
using Stackarr.Core.Models;
using Stackarr.Core.Services;
using Stackarr.Docker;

namespace Stackarr.Orchestration;

public class StackarrOrchestrator(
    IOptionsMonitor<StackarrConfig> configMonitor,
    IConfiguration configuration,
    ServiceProvisioner provisioner,
    StackarrProvisioner stackarrProvisioner,
    ResourceInventory inventory,
    HealthMonitor healthMonitor,
    DockerMetricsCollector metricsCollector,
    ServiceRegistry serviceRegistry,
    ImageManager imageManager,
    StackarrInstance stackarr,
    ILogger<StackarrOrchestrator> logger)
    : BackgroundService
{
    readonly ConcurrentDictionary<string, ServiceRecoveryState> _recoveryStates = new();
    readonly TimeSpan _recoveryCheckInterval = TimeSpan.FromSeconds(10);
    const int MaxRecoveryAttempts = 5;

    DashboardMetrics _latestMetrics = new();
    List<VolumeMetrics> _lastVolumeMetrics = [];

    public event Action<DashboardMetrics>? MetricsUpdated;

    public DashboardMetrics LatestMetrics => _latestMetrics;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Stackarr orchestrator starting...");

        try
        {
            StackarrConfig config = configMonitor.CurrentValue;
            await stackarrProvisioner.Provision(config, stoppingToken);
            await ProvisionAllServices(config, stoppingToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed during initial provisioning, will retry via health monitor");
        }

        _ = MonitorAndRecover(stoppingToken);
        _ = CollectMetrics(stoppingToken);
        _ = CleanupOrphans(stoppingToken);

        healthMonitor.HealthChanged += OnHealthChanged;

        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        healthMonitor.HealthChanged -= OnHealthChanged;
        await base.StopAsync(cancellationToken);

        if (configMonitor.CurrentValue.StopContainersOnShutdown)
        {
            logger.LogInformation("Stackarr shutting down; stopping managed service containers (not removing)...");
            try
            {
                using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(120));
                await StopManagedContainersAsync(timeout.Token);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error stopping managed containers during shutdown");
            }
        }
    }

    async Task StopManagedContainersAsync(CancellationToken ct)
    {
        List<ContainerInfo> containers = await inventory.GetOwnedContainers(ct);

        List<Task> stopTasks = containers
            .Where(c => c.State == ContainerState.Running)
            .Select(async container =>
            {
                logger.LogInformation("Stopping managed container {Container}", container.Name);
                await provisioner.StopContainer(container.Name, ct);
            })
            .ToList();

        await Task.WhenAll(stopTasks);
    }

    public async Task ProvisionAllServices(StackarrConfig config, CancellationToken ct = default)
    {
        logger.LogInformation("Provisioning all services...");

        VolumePathResolver volumeResolver = new(config.Storage);
        List<string> enabledServices = ConfigSyncer.GetEnabledServices(configuration, serviceRegistry);

        foreach (List<string> serviceGroup in GetDependencyOrder(enabledServices))
        {
            foreach (string serviceName in serviceGroup)
            {
                Service? service = serviceRegistry.Get(serviceName);
                if (service == null)
                    continue;

                try
                {
                    IReadOnlyList<ContainerSpec> specs = service.Definition.BuildContainerSpecs(configuration, volumeResolver, stackarr);
                    foreach (ContainerSpec spec in specs)
                    {
                        logger.LogInformation(
                            "Provisioning service: {Service} ({Container})",
                            serviceName, spec.Name);
                        await provisioner.ProvisionContainer(spec, ct);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to provision service {Service}", serviceName);
                }
            }
        }

        logger.LogInformation("All services provisioned");
    }

    /// <summary>
    /// Force-restarts every container of a service by its key. Used by watchdogs to reset a service
    /// whose internal state is stuck (e.g. slskd's Soulseek reconnect backoff) without recreating it.
    /// </summary>
    public async Task<bool> RestartServiceAsync(string serviceKey, CancellationToken ct = default)
    {
        Service? service = serviceRegistry.Get(serviceKey);
        if (service == null)
        {
            logger.LogWarning("Service {Service} not found in registry", serviceKey);
            return false;
        }

        StackarrConfig config = configMonitor.CurrentValue;
        VolumePathResolver volumeResolver = new(config.Storage);
        IReadOnlyList<ContainerSpec> specs = service.Definition.BuildContainerSpecs(configuration, volumeResolver, stackarr);

        bool any = false;
        foreach (ContainerSpec spec in specs)
            any |= await provisioner.RestartContainer(spec.Name, ct);

        if (any)
            logger.LogWarning("Restarted service {Service} ({Count} container(s))", serviceKey, specs.Count);
        return any;
    }

    public async Task RecoverService(string serviceName, CancellationToken ct = default)
    {
        ServiceRecoveryState state = _recoveryStates.GetOrAdd(serviceName, _ => new());

        if (state.Attempts >= MaxRecoveryAttempts)
        {
            TimeSpan backoff = CalculateBackoff(state.Attempts);
            if (DateTime.UtcNow - state.LastAttemptTime < backoff)
            {
                logger.LogDebug("Service {Service} in backoff period, skipping recovery", serviceName);
                return;
            }
        }

        state.Attempts++;
        state.LastAttemptTime = DateTime.UtcNow;

        logger.LogWarning("Attempting recovery of service {Service} (attempt {Attempt}/{Max})",
            serviceName, state.Attempts, MaxRecoveryAttempts);

        try
        {
            Service? service = serviceRegistry.Get(serviceName);
            if (service == null)
            {
                logger.LogWarning("Service {Service} not found in registry", serviceName);
                return;
            }

            StackarrConfig config = configMonitor.CurrentValue;
            VolumePathResolver volumeResolver = new(config.Storage);
            IReadOnlyList<ContainerSpec> specs = service.Definition.BuildContainerSpecs(configuration, volumeResolver, stackarr);
            bool anyRecovered = false;

            foreach (ContainerSpec spec in specs)
            {
                bool running = await provisioner.EnsureContainerRunning(spec.Name, ct);
                if (running)
                {
                    logger.LogInformation(
                        "Service {Service} ({Container}) recovered successfully",
                        serviceName, spec.Name);
                    anyRecovered = true;
                }
                else
                {
                    logger.LogWarning("Container {Container} not found, attempting re-provisioning", spec.Name);
                    await provisioner.ProvisionContainer(spec, ct);
                    logger.LogInformation(
                        "Service {Service} ({Container}) re-provisioned",
                        serviceName, spec.Name);
                    anyRecovered = true;
                }
            }

            if (anyRecovered)
            {
                state.Attempts = 0;
                state.ConsecutiveFailures = 0;
            }
        }
        catch (Exception ex)
        {
            state.ConsecutiveFailures++;
            logger.LogError(ex, "Failed to recover service {Service}", serviceName);
        }
    }

    async Task MonitorAndRecover(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_recoveryCheckInterval, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                List<ContainerInfo> containers = await inventory.GetOwnedContainers(ct);
                StackarrConfig config = configMonitor.CurrentValue;
                List<string> enabledServices = ConfigSyncer.GetEnabledServices(configuration, serviceRegistry);

                foreach (ContainerInfo container in containers)
                {
                    if (!container.Labels.TryGetValue(ContainerLabels.Service, out string? serviceKey))
                        continue;

                    if (!enabledServices.Contains(serviceKey))
                        continue;

                    if (imageManager.IsImageBeingRepulled(container.Image))
                        continue;

                    if (container.State != ContainerState.Running)
                    {
                        logger.LogWarning("Service {Service} is not running (state: {State}), initiating recovery",
                            serviceKey, container.State);
                        await RecoverService(serviceKey, ct);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error during recovery monitoring");
            }
        }
    }

    async Task CollectMetrics(CancellationToken ct)
    {
        int volumeMetricsCounter = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                StackarrConfig config = configMonitor.CurrentValue;
                List<string> enabledServices = ConfigSyncer.GetEnabledServices(configuration, serviceRegistry);

                List<ServiceDashboardInfo> services = await metricsCollector.GetServiceMetrics(enabledServices, ct);
                List<NetworkInterfaceMetrics> networkInterfaces = await metricsCollector.GetNetworkMetrics(ct);

                volumeMetricsCounter++;
                List<VolumeMetrics> volumes;
                if (volumeMetricsCounter % 6 == 0)
                {
                    volumes = await metricsCollector.GetVolumeMetrics(ct);
                    _lastVolumeMetrics = volumes;
                }
                else
                {
                    volumes = _lastVolumeMetrics;
                }

                List<ContainerInfo> allContainers = await inventory.GetOwnedContainers(ct);

                _latestMetrics = new()
                {
                    Services = services,
                    NetworkInterfaces = networkInterfaces,
                    Volumes = volumes,
                    TotalContainers = allContainers.Count,
                    RunningContainers = allContainers.Count(c => c.State == ContainerState.Running),
                    HealthyContainers = services.Count(s => s.IsHealthy),
                    UnhealthyContainers = services.Count(s => !s.IsHealthy && s.State == ContainerState.Running),
                    LastUpdated = DateTime.UtcNow
                };

                MetricsUpdated?.Invoke(_latestMetrics);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error collecting metrics");
            }
        }
    }

    static IEnumerable<List<string>> GetDependencyOrder(List<string> services)
    {
        List<string> traefikServices = services.Where(s => s == "reverse_proxy").ToList();
        List<string> vpnServices = services.Where(s => s == "vpn").ToList();
        List<string> dependentServices = services.Where(s => s != "reverse_proxy" && s != "vpn").ToList();

        if (traefikServices.Count > 0)
            yield return traefikServices;
        if (vpnServices.Count > 0)
            yield return vpnServices;
        if (dependentServices.Count > 0)
            yield return dependentServices;
    }

    static TimeSpan CalculateBackoff(int attempt)
    {
        double seconds = Math.Pow(2, Math.Min(attempt, 6)) * 10;
        return TimeSpan.FromSeconds(Math.Min(seconds, 300));
    }

    void OnHealthChanged(string containerName, Core.Enums.ContainerState newState)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(500);
                await CollectSingleMetricsSnapshot(CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error collecting metrics after health change for {Container}", containerName);
            }
        });
    }

    async Task CollectSingleMetricsSnapshot(CancellationToken ct)
    {
        StackarrConfig config = configMonitor.CurrentValue;
        List<string> enabledServices = ConfigSyncer.GetEnabledServices(configuration, serviceRegistry);

        List<ServiceDashboardInfo> services = await metricsCollector.GetServiceMetrics(enabledServices, ct);
        List<NetworkInterfaceMetrics> networkInterfaces = await metricsCollector.GetNetworkMetrics(ct);

        List<ContainerInfo> allContainers = await inventory.GetOwnedContainers(ct);

        _latestMetrics = new()
        {
            Services = services,
            NetworkInterfaces = networkInterfaces,
            Volumes = _lastVolumeMetrics,
            TotalContainers = allContainers.Count,
            RunningContainers = allContainers.Count(c => c.State == Core.Enums.ContainerState.Running),
            HealthyContainers = services.Count(s => s.IsHealthy),
            UnhealthyContainers = services.Count(s => !s.IsHealthy && s.State == Core.Enums.ContainerState.Running),
            LastUpdated = DateTime.UtcNow
        };

        MetricsUpdated?.Invoke(_latestMetrics);
    }

    async Task CleanupOrphans(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(5), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                await inventory.CleanupOrphanedResources(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error during orphan cleanup");
            }
        }
    }
}

class ServiceRecoveryState
{
    public int Attempts { get; set; }
    public int ConsecutiveFailures { get; set; }
    public DateTime LastAttemptTime { get; set; }
}
