using System.Runtime.InteropServices;
using Arrk.Core.Models;

namespace Arrk.Orchestration;

public class HostMetricsService
{
    long _lastTotalJiffies;
    long _lastIdleJiffies;
    DateTime _lastCpuRead = DateTime.UtcNow;

    public HostMetrics GetHostMetrics()
    {
        double cpuPercent = GetCpuPercent();
        GetMemoryInfo(out long memTotal, out long memUsed, out double memPercent);

        return new()
        {
            CpuPercent = cpuPercent,
            MemoryTotalBytes = memTotal,
            MemoryUsedBytes = memUsed,
            MemoryPercent = memPercent
        };
    }

    double GetCpuPercent()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return 0;

        try
        {
            if (!File.Exists("/proc/stat"))
                return 0;

            string? line = File.ReadLines("/proc/stat").FirstOrDefault(l => l.StartsWith("cpu "));
            if (line == null)
                return 0;

            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5)
                return 0;

            long idle = long.Parse(parts[4]);
            long total = 0;
            for (int i = 1; i < parts.Length; i++)
                total += long.Parse(parts[i]);

            DateTime now = DateTime.UtcNow;
            double elapsed = (now - _lastCpuRead).TotalSeconds;

            if (elapsed < 0.1)
                return _lastTotalJiffies > 0 ? CalculatePreviousCpuPercent() : 0;

            long totalDiff = total - _lastTotalJiffies;
            long idleDiff = idle - _lastIdleJiffies;
            _lastTotalJiffies = total;
            _lastIdleJiffies = idle;
            _lastCpuRead = now;

            return totalDiff > 0 ? (double)(totalDiff - idleDiff) / totalDiff * 100 : 0;
        }
        catch
        {
            return 0;
        }
    }

    double CalculatePreviousCpuPercent()
    {
        return _lastTotalJiffies > 0 ? 0 : 0;
    }

    static void GetMemoryInfo(out long totalBytes, out long usedBytes, out double percent)
    {
        totalBytes = 0;
        usedBytes = 0;
        percent = 0;

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;

        try
        {
            if (!File.Exists("/proc/meminfo"))
                return;

            string[] lines = File.ReadAllLines("/proc/meminfo");
            long total = 0, available = 0;

            foreach (string line in lines)
            {
                if (line.StartsWith("MemTotal:"))
                    total = ParseMemInfoKb(line);
                else if (line.StartsWith("MemAvailable:"))
                    available = ParseMemInfoKb(line);
            }

            totalBytes = total * 1024;
            usedBytes = (total - available) * 1024;
            percent = total > 0 ? (double)(total - available) / total * 100 : 0;
        }
        catch
        {
        }
    }

    static long ParseMemInfoKb(string line)
    {
        string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && long.TryParse(parts[1], out long val) ? val : 0;
    }
}
