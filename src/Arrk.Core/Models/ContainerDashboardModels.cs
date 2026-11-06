using Arrk.Core.Enums;

namespace Arrk.Core.Models;

public class ServiceDashboardInfo
{
    public required string ServiceName { get; init; }
    public required string ContainerName { get; init; }
    public string Image { get; set; } = "";
    public ContainerState State { get; set; }
    public bool IsHealthy { get; set; }
    public bool Enabled { get; set; }
    public DateTime LastChecked { get; set; }
    public string? ErrorMessage { get; set; }
    public ContainerMetrics? Metrics { get; set; }
}

public class ContainerMetrics
{
    public double CpuPercent { get; set; }
    public long MemoryUsageBytes { get; set; }
    public long MemoryLimitBytes { get; set; }
    public double MemoryPercent { get; set; }
    public long NetworkRxBytes { get; set; }
    public long NetworkTxBytes { get; set; }
    public double NetworkRxRateBytesPerSec { get; set; }
    public double NetworkTxRateBytesPerSec { get; set; }
    public DateTime Timestamp { get; set; }
}

public class DashboardMetrics
{
    public List<ServiceDashboardInfo> Services { get; set; } = [];
    public List<NetworkInterfaceMetrics> NetworkInterfaces { get; set; } = [];
    public List<VolumeMetrics> Volumes { get; set; } = [];
    public int TotalContainers { get; set; }
    public int RunningContainers { get; set; }
    public int HealthyContainers { get; set; }
    public int UnhealthyContainers { get; set; }
    public DateTime LastUpdated { get; set; }
}

public class NetworkInterfaceMetrics
{
    public string InterfaceName { get; set; } = "";
    public long RxBytes { get; set; }
    public long TxBytes { get; set; }
    public long RxPackets { get; set; }
    public long TxPackets { get; set; }
    public double RxRateBytesPerSec { get; set; }
    public double TxRateBytesPerSec { get; set; }
}

public class VolumeMetrics
{
    public string Name { get; set; } = "";
    public string MountPoint { get; set; } = "";
    public long TotalBytes { get; set; }
    public long UsedBytes { get; set; }
    public long AvailableBytes { get; set; }
    public double UsedPercent { get; set; }
    public List<string> Containers { get; set; } = [];
}

public class SystemMetrics
{
    public long TotalMemoryBytes { get; set; }
    public long UsedMemoryBytes { get; set; }
    public double CpuPercent { get; set; }
    public int TotalContainers { get; set; }
    public int RunningContainers { get; set; }
    public int StoppedContainers { get; set; }
}
