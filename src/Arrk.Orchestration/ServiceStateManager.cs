using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Arrk.Core.Configuration;
using Arrk.Core.Enums;
using Arrk.Core.Models;
using Arrk.Core.Services;
using Arrk.Docker;

namespace Arrk.Orchestration;

public class ServiceStateManager(
    ResourceInventory inventory,
    ServiceRegistry serviceRegistry,
    DockerMetricsCollector metricsCollector,
    IOptionsMonitor<ArrkConfig> configMonitor,
    IConfiguration configuration,
    ILogger<ServiceStateManager> logger)
{
    DateTime _lastRefresh = DateTime.MinValue;
    readonly TimeSpan _cacheDuration = TimeSpan.FromSeconds(3);

    DateTime _lastMetricsRefresh = DateTime.MinValue;
    readonly TimeSpan _metricsCacheDuration = TimeSpan.FromSeconds(10);
    readonly ConcurrentDictionary<string, ServiceMetrics> _metricsCache = new();

    public async Task<List<Service>> GetAllServices(CancellationToken ct = default)
    {
        if (DateTime.UtcNow - _lastRefresh < _cacheDuration && serviceRegistry.All.All(s => s.State.LastChecked != DateTime.MinValue))
            return BuildServiceList();

        try
        {
            ArrkConfig config = configMonitor.CurrentValue;
            List<ContainerInfo> allContainers = await inventory.GetOwnedContainers(ct);

            foreach (Service service in serviceRegistry.All)
            {
                bool isEnabled = service.Definition.IsEnabled(configuration);

                List<ContainerInfo> serviceContainers = allContainers.Where(c =>
                    c.Labels.TryGetValue(ContainerLabels.Service, out string? s) &&
                    s.Equals(service.Definition.Key, StringComparison.OrdinalIgnoreCase)
                ).ToList();

                ContainerState aggregateState = ComputeAggregateState(serviceContainers);

                _metricsCache.TryGetValue(service.Definition.Key, out ServiceMetrics? cachedMetrics);

                service.State = new ServiceState
                {
                    ServiceKey = service.Definition.Key,
                    IsEnabled = isEnabled,
                    Containers = serviceContainers,
                    AggregateState = aggregateState,
                    LastChecked = DateTime.UtcNow,
                    Metrics = cachedMetrics
                };
            }

            _lastRefresh = DateTime.UtcNow;
            return BuildServiceList();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get service states");
            return BuildServiceList();
        }
    }

    public async Task RefreshMetrics(CancellationToken ct = default)
    {
        if (DateTime.UtcNow - _lastMetricsRefresh < _metricsCacheDuration)
            return;

        try
        {
            List<Task> tasks = new();
            foreach (Service service in serviceRegistry.All)
            {
                List<ContainerInfo> runningContainers = service.State.Containers
                    .Where(c => c.State == ContainerState.Running)
                    .ToList();

                if (runningContainers.Count == 0)
                {
                    _metricsCache.TryRemove(service.Definition.Key, out _);
                    continue;
                }

                tasks.Add(ComputeAndCacheServiceMetrics(service, runningContainers, ct));

            }

            await Task.WhenAll(tasks);
            _lastMetricsRefresh = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to refresh metrics");
        }
    }

    async Task ComputeAndCacheServiceMetrics(Service service, List<ContainerInfo> runningContainers, CancellationToken ct)
    {
        IEnumerable<Task<ContainerMetrics?>> statsTasks = runningContainers.Select(c => metricsCollector.GetContainerStats(c.Id, ct));
        ContainerMetrics?[] allStats = await Task.WhenAll(statsTasks);

        double totalCpu = 0;
        long totalMemUsed = 0;
        long totalMemLimit = 0;
        long totalRx = 0;
        long totalTx = 0;
        double totalRxRate = 0;
        double totalTxRate = 0;

        foreach (ContainerMetrics? stats in allStats)
        {
            if (stats != null)
            {
                totalCpu += stats.CpuPercent;
                totalMemUsed += stats.MemoryUsageBytes;
                totalMemLimit += stats.MemoryLimitBytes;
                totalRx += stats.NetworkRxBytes;
                totalTx += stats.NetworkTxBytes;
                totalRxRate += stats.NetworkRxRateBytesPerSec;
                totalTxRate += stats.NetworkTxRateBytesPerSec;
            }
        }

        ServiceMetrics metrics = new()
        {
            RunningContainerCount = runningContainers.Count,
            CpuPercent = totalCpu,
            MemoryUsageBytes = totalMemUsed,
            MemoryLimitBytes = totalMemLimit,
            MemoryPercent = totalMemLimit > 0 ? (double)totalMemUsed / totalMemLimit * 100 : 0,
            NetworkRxBytes = totalRx,
            NetworkTxBytes = totalTx,
            NetworkRxRateBytesPerSec = totalRxRate,
            NetworkTxRateBytesPerSec = totalTxRate
        };

        _metricsCache[service.Definition.Key] = metrics;
    }

    public async Task<Service?> GetService(string serviceKey, CancellationToken ct = default)
    {
        if (DateTime.UtcNow - _lastRefresh < _cacheDuration)
            return serviceRegistry.Get(serviceKey);

        List<Service> services = await GetAllServices(ct);
        return services.FirstOrDefault(s => s.Definition.Key == serviceKey);
    }

    public void InvalidateCache()
    {
        _lastRefresh = DateTime.MinValue;
        _lastMetricsRefresh = DateTime.MinValue;
        _metricsCache.Clear();
    }

    List<Service> BuildServiceList()
    {
        return [.. serviceRegistry.All];
    }

    static ContainerState ComputeAggregateState(List<ContainerInfo> containers)
    {
        if (containers.Count == 0)
            return ContainerState.Unknown;

        if (containers.Any(c => c.State == ContainerState.Running))
            return ContainerState.Running;

        ContainerInfo? firstNonRunning = containers.FirstOrDefault(c => c.State != ContainerState.Unknown);
        return firstNonRunning?.State ?? ContainerState.Unknown;
    }
}
