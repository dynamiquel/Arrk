namespace Stackarr.Core.Models;

public class ServiceMetrics
{
    public int RunningContainerCount { get; init; }
    public double CpuPercent { get; init; }
    public long MemoryUsageBytes { get; init; }
    public long MemoryLimitBytes { get; init; }
    public double MemoryPercent { get; init; }
    public long NetworkRxBytes { get; init; }
    public long NetworkTxBytes { get; init; }
    public double NetworkRxRateBytesPerSec { get; init; }
    public double NetworkTxRateBytesPerSec { get; init; }
}
