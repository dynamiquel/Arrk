namespace Arrk.Core.Models;

public class ContainerStats
{
    public int Id { get; set; }
    public string ContainerId { get; set; } = "";
    public string ContainerName { get; set; } = "";
    public double CpuPercent { get; set; }
    public long MemoryUsageBytes { get; set; }
    public long MemoryLimitBytes { get; set; }
    public double MemoryPercent { get; set; }
    public long NetworkRxBytes { get; set; }
    public long NetworkTxBytes { get; set; }
    public DateTime Timestamp { get; set; }
}
