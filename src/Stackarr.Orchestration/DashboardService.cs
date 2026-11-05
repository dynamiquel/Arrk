using Microsoft.Extensions.Logging;
using Stackarr.Core.Enums;
using Stackarr.Core.Models;
using Stackarr.Core.Services;
using Stackarr.Docker;

namespace Stackarr.Orchestration;

public class DashboardService(
    ServiceStateManager serviceStateManager,
    HostMetricsService hostMetricsService,
    DockerMetricsCollector metricsCollector,
    ILogger<DashboardService> logger)
{
    List<NetworkInterfaceMetrics>? _cachedNetwork;
    List<VolumeMetrics>? _cachedVolumes;
    DateTime _lastDockerOverview = DateTime.MinValue;
    readonly TimeSpan _overviewCacheDuration = TimeSpan.FromSeconds(15);

    public async Task<DashboardSummary> GetDashboardSummary(CancellationToken ct = default)
    {
        try
        {
            List<Service> services = await serviceStateManager.GetAllServices(ct);
            HostMetrics hostMetrics = hostMetricsService.GetHostMetrics();

            if (DateTime.UtcNow - _lastDockerOverview >= _overviewCacheDuration)
            {
                try
                {
                    Task<List<NetworkInterfaceMetrics>> networkTask = metricsCollector.GetNetworkMetrics(ct);
                    Task<List<VolumeMetrics>> volumesTask = metricsCollector.GetVolumeMetrics(ct);
                    await Task.WhenAll(networkTask, volumesTask);
                    _cachedNetwork = networkTask.Result;
                    _cachedVolumes = volumesTask.Result;
                    _lastDockerOverview = DateTime.UtcNow;
                }
                catch { }
            }

            double totalCpu = 0;
            long totalMemUsed = 0;
            long totalMemLimit = 0;

            foreach (Service svc in services)
            {
                if (svc.State.Metrics != null)
                {
                    totalCpu += svc.State.Metrics.CpuPercent;
                    totalMemUsed += svc.State.Metrics.MemoryUsageBytes;
                    totalMemLimit += svc.State.Metrics.MemoryLimitBytes;
                }
            }

            return new()
            {
                Services = services,
                HostMetrics = hostMetrics,
                DockerMetrics = new()
                {
                    TotalContainers = services.Sum(s => s.State.Containers.Count),
                    RunningContainers = services.Sum(s => s.State.Containers.Count(c => c.State == ContainerState.Running)),
                    HealthyContainers = services.Count(s => s.State.IsHealthy),
                    UnhealthyContainers = services.Count(s => s.State.IsEnabled && !s.State.IsHealthy),
                    CpuPercent = totalCpu,
                    MemoryUsageBytes = totalMemUsed,
                    MemoryLimitBytes = totalMemLimit,
                    MemoryPercent = totalMemLimit > 0 ? (double)totalMemUsed / totalMemLimit * 100 : 0,
                    NetworkInterfaces = _cachedNetwork ?? [],
                    Volumes = _cachedVolumes ?? []
                },
                LastUpdated = DateTime.UtcNow
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get dashboard summary");
            return new() { LastUpdated = DateTime.UtcNow };
        }
    }
}
