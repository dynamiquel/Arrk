using Stackarr.Core.Services;

namespace Stackarr.Core.Models;

public class DashboardSummary
{
    public List<Service> Services { get; init; } = [];
    public HostMetrics? HostMetrics { get; init; }
    public DockerMetrics DockerMetrics { get; init; } = new();
    public DateTime LastUpdated { get; init; }
}

public class DockerMetrics
{
    public int TotalContainers { get; init; }
    public int RunningContainers { get; init; }
    public int HealthyContainers { get; init; }
    public int UnhealthyContainers { get; init; }
    public double CpuPercent { get; init; }
    public long MemoryUsageBytes { get; init; }
    public long MemoryLimitBytes { get; init; }
    public double MemoryPercent { get; init; }
    public List<NetworkInterfaceMetrics> NetworkInterfaces { get; init; } = [];
    public List<VolumeMetrics> Volumes { get; init; } = [];
}
