namespace Stackarr.Core.Models;

public class HostMetrics
{
    public double CpuPercent { get; init; }
    public long MemoryTotalBytes { get; init; }
    public long MemoryUsedBytes { get; init; }
    public double MemoryPercent { get; init; }
}
