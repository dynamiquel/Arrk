using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Docker.DotNet;
using Docker.DotNet.Models;
using Stackarr.Core.Models;
using Stackarr.Core.Services;
using ContainerState = Stackarr.Core.Enums.ContainerState;

namespace Stackarr.Docker;

public class DockerMetricsCollector(
    DockerClient client,
    ContainerManager containerManager,
    NetworkManager networkManager,
    ServiceRegistry registry)
{
    readonly NetworkManager _networkManager = networkManager;
    readonly ConcurrentDictionary<string, (long RxBytes, long TxBytes, DateTime Timestamp)> _previousNetworkSamples = new();

    public async Task<List<ServiceDashboardInfo>> GetServiceMetrics(
        IReadOnlyList<string> serviceNames,
        CancellationToken ct = default)
    {
        List<ContainerInfo> allContainers = await containerManager.ListContainers(
            labelFilter: ContainerLabels.Managed, ct: ct);

        List<Task<ServiceDashboardInfo?>> tasks = new List<Task<ServiceDashboardInfo?>>();
        foreach (string serviceName in serviceNames)
        {
            Service? service = registry.Get(serviceName);
            string displayName = service?.Definition.DisplayName ?? serviceName;
            List<ContainerInfo> serviceContainers = allContainers.Where(c =>
                c.Labels.TryGetValue(ContainerLabels.Service, out string? s) &&
                s.Equals(serviceName, StringComparison.OrdinalIgnoreCase)
            ).ToList();
            tasks.Add(GetServiceInfo(serviceName, displayName, serviceContainers, ct));
        }

        ServiceDashboardInfo?[] results = await Task.WhenAll(tasks);
        return results.Where(r => r != null).ToList()!;
    }

    async Task<ServiceDashboardInfo?> GetServiceInfo(
        string serviceName,
        string displayName,
        List<ContainerInfo> containers,
        CancellationToken ct)
    {
        if (containers.Count == 0)
            return null;

        ServiceDashboardInfo? bestInfo = null;

        foreach (ContainerInfo container in containers)
        {
            ServiceDashboardInfo info = new ServiceDashboardInfo
            {
                ServiceName = displayName,
                ContainerName = container.Name,
                Image = container.Image ?? "",
                State = container.State,
                Enabled = true,
                LastChecked = DateTime.UtcNow
            };

            ContainerState effectiveState = await GetEffectiveState(container, ct);
            info.State = effectiveState;
            info.IsHealthy = effectiveState == Core.Enums.ContainerState.Running;

            if (effectiveState == Core.Enums.ContainerState.Running)
            {
                try
                {
                    ContainerMetrics? stats = await GetContainerStats(container.Id, ct);
                    if (stats != null)
                        info.Metrics = stats;
                }
                catch { }
            }

            if (bestInfo == null)
            {
                bestInfo = info;
            }
            else
            {
                if (info.State == Core.Enums.ContainerState.Running)
                    bestInfo = info;
                else if (bestInfo.State != Core.Enums.ContainerState.Running &&
                         info.State != Core.Enums.ContainerState.Running &&
                         info.State != Core.Enums.ContainerState.Unknown)
                    bestInfo = info;
            }
        }

        return bestInfo;
    }

    async Task<Core.Enums.ContainerState> GetEffectiveState(ContainerInfo container, CancellationToken ct)
    {
        if (container.State != Core.Enums.ContainerState.Running)
            return container.State;

        try
        {
            ContainerInspectResponse inspect = await containerManager.InspectContainer(container.Id, ct);
            if (inspect.State.Health != null)
            {
                return inspect.State.Health.Status?.ToLowerInvariant() switch
                {
                    "healthy" => Core.Enums.ContainerState.Running,
                    "unhealthy" => Core.Enums.ContainerState.Running,
                    "starting" => Core.Enums.ContainerState.Starting,
                    _ => Core.Enums.ContainerState.Running
                };
            }
        }
        catch { }

        return container.State;
    }

    public async Task<ContainerMetrics?> GetContainerStats(string containerId, CancellationToken ct = default)
    {
        try
        {
            ContainerStatsParameters statsParams = new()
            {
                Stream = false
            };

#pragma warning disable CS0618
            using Stream? stream = await client.Containers.GetContainerStatsAsync(containerId, statsParams, ct);
#pragma warning restore CS0618
            using StreamReader reader = new StreamReader(stream);
            string json = await reader.ReadToEndAsync(ct);

            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            double cpuPercent = 0.0;
            if (root.TryGetProperty("cpu_stats", out JsonElement cpuStats) &&
                root.TryGetProperty("precpu_stats", out JsonElement preCpuStats))
            {
                cpuPercent = CalculateCpuPercent(cpuStats, preCpuStats);
            }

            long memUsage = 0;
            long memLimit = 0;
            double memPercent = 0;
            if (root.TryGetProperty("memory_stats", out JsonElement memStats))
            {
                memUsage = memStats.TryGetProperty("usage", out JsonElement usage) ? usage.GetInt64() : 0;
                memLimit = memStats.TryGetProperty("limit", out JsonElement limit) ? limit.GetInt64() : 0;
                memPercent = memLimit > 0 ? (double)memUsage / memLimit * 100 : 0;
            }

            long rxBytes = 0;
            long txBytes = 0;
            if (root.TryGetProperty("networks", out JsonElement networks))
            {
                foreach (JsonProperty prop in networks.EnumerateObject())
                {
                    if (prop.Value.TryGetProperty("rx_bytes", out JsonElement rx))
                        rxBytes += rx.GetInt64();
                    if (prop.Value.TryGetProperty("tx_bytes", out JsonElement tx))
                        txBytes += tx.GetInt64();
                }
            }

            DateTime now = DateTime.UtcNow;
            (long prevRx, long prevTx, DateTime prevTime) = _previousNetworkSamples.GetValueOrDefault(containerId, (0, 0, now));
            double elapsed = (now - prevTime).TotalSeconds;
            double rxRate = elapsed > 0 ? (rxBytes - prevRx) / elapsed : 0;
            double txRate = elapsed > 0 ? (txBytes - prevTx) / elapsed : 0;
            _previousNetworkSamples[containerId] = (rxBytes, txBytes, now);

            return new()
            {
                CpuPercent = cpuPercent,
                MemoryUsageBytes = memUsage,
                MemoryLimitBytes = memLimit,
                MemoryPercent = memPercent,
                NetworkRxBytes = rxBytes,
                NetworkTxBytes = txBytes,
                NetworkRxRateBytesPerSec = rxRate,
                NetworkTxRateBytesPerSec = txRate,
                Timestamp = now
            };
        }
        catch
        {
            return null;
        }
    }

    public async Task<List<VolumeMetrics>> GetVolumeMetrics(CancellationToken ct = default)
    {
        List<VolumeMetrics> volumes = new List<VolumeMetrics>();

        try
        {
            List<ContainerInfo> ownedContainers = await containerManager.ListContainers(
                labelFilter: ContainerLabels.Managed, ct: ct);

            Dictionary<string, List<string>> volumeToContainers = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (ContainerInfo container in ownedContainers)
            {
                try
                {
                    ContainerInspectResponse? inspect = await containerManager.InspectContainer(container.Id, ct);
                    if (inspect?.Mounts == null)
                        continue;

                    foreach (MountPoint? mount in inspect.Mounts.Where(m => m.Type == "volume" && !string.IsNullOrEmpty(m.Name)))
                    {
                        if (!volumeToContainers.TryGetValue(mount.Name, out List<string>? list))
                        {
                            list = [];
                            volumeToContainers[mount.Name] = list;
                        }
                        list.Add(container.Name);
                    }
                }
                catch { }
            }

            VolumesListResponse? response = await client.Volumes.ListAsync(new(), ct);

            foreach (VolumeResponse? vol in response.Volumes ?? [])
            {
                string volName = vol.Name ?? "";
                if (!volumeToContainers.ContainsKey(volName))
                    continue;

                string mountPoint = vol.Mountpoint ?? "";
                List<string> containerNames = volumeToContainers[volName];

                long totalBytes = 0;
                long usedBytes = 0;
                long availableBytes = 0;
                double usedPercent = 0;

                if (!string.IsNullOrEmpty(mountPoint) && RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    try
                    {
                        if (GetDiskSpace(mountPoint, out long total, out long avail, out long used))
                        {
                            totalBytes = total;
                            availableBytes = avail;
                            usedBytes = used;
                            usedPercent = total > 0 ? (double)used / total * 100 : 0;
                        }
                    }
                    catch { }
                }

                volumes.Add(new()
                {
                    Name = volName,
                    MountPoint = mountPoint,
                    TotalBytes = totalBytes,
                    UsedBytes = usedBytes,
                    AvailableBytes = availableBytes,
                    UsedPercent = usedPercent,
                    Containers = containerNames
                });
            }
        }
        catch { }

        return volumes;
    }

    public async Task<List<NetworkInterfaceMetrics>> GetNetworkMetrics(CancellationToken ct = default)
    {
        List<NetworkInterfaceMetrics> metrics = new List<NetworkInterfaceMetrics>();

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && File.Exists("/proc/net/dev"))
            {
                string[] lines = await File.ReadAllLinesAsync("/proc/net/dev", ct);
                foreach (string line in lines.Skip(2))
                {
                    string[] parts = line.Split(':', 2);
                    if (parts.Length != 2)
                        continue;

                    string iface = parts[0].Trim();
                    if (iface == "lo")
                        continue;

                    string[] values = parts[1].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (values.Length < 10)
                        continue;

                    if (long.TryParse(values[0], out long rxBytes) &&
                        long.TryParse(values[1], out long rxPackets) &&
                        long.TryParse(values[8], out long txBytes) &&
                        long.TryParse(values[9], out long txPackets))
                    {
                        string key = $"iface_{iface}";
                        (long prevRx, long prevTx, DateTime prevTime) = _previousNetworkSamples.GetValueOrDefault(
                            key, (0, 0, DateTime.UtcNow));
                        double elapsed = (DateTime.UtcNow - prevTime).TotalSeconds;
                        double rxRate = elapsed > 0 ? (rxBytes - prevRx) / elapsed : 0;
                        double txRate = elapsed > 0 ? (txBytes - prevTx) / elapsed : 0;
                        _previousNetworkSamples[key] = (rxBytes, txBytes, DateTime.UtcNow);

                        metrics.Add(new()
                        {
                            InterfaceName = iface,
                            RxBytes = rxBytes,
                            TxBytes = txBytes,
                            RxPackets = rxPackets,
                            TxPackets = txPackets,
                            RxRateBytesPerSec = rxRate,
                            TxRateBytesPerSec = txRate
                        });
                    }
                }
            }
        }
        catch { }

        return metrics;
    }

    static double CalculateCpuPercent(JsonElement cpuStats, JsonElement preCpuStats)
    {
        try
        {
            long cpuDelta = cpuStats.TryGetProperty("cpu_usage", out JsonElement cpuUsage)
                ? cpuUsage.TryGetProperty("total_usage", out JsonElement total) ? total.GetInt64() : 0 : 0;
            long preDelta = preCpuStats.TryGetProperty("cpu_usage", out JsonElement preCpuUsage)
                ? preCpuUsage.TryGetProperty("total_usage", out JsonElement preTotal) ? preTotal.GetInt64() : 0 : 0;

            long systemDelta = cpuStats.TryGetProperty("system_cpu_usage", out JsonElement sysCpu)
                ? sysCpu.GetInt64() : 0;
            long preSystemDelta = preCpuStats.TryGetProperty("system_cpu_usage", out JsonElement preSysCpu)
                ? preSysCpu.GetInt64() : 0;

            long numCpus = cpuStats.TryGetProperty("online_cpus", out JsonElement onlineCpus)
                ? onlineCpus.GetInt64() : 1;

            if (systemDelta > 0 && preSystemDelta > 0)
            {
                return ((double)(cpuDelta - preDelta) / (systemDelta - preSystemDelta)) * numCpus * 100.0;
            }
        }
        catch { }

        return 0;
    }

    [DllImport("libc", SetLastError = true)]
    static extern int statvfs(string path, out StatVFS buf);

    public static bool GetDiskSpace(string path, out long total, out long available, out long used)
    {
        total = 0;
        available = 0;
        used = 0;

        if (!Directory.Exists(path))
        {
            return false;
        }

        if (statvfs(path, out StatVFS stat) == 0)
        {
            try
            {
                total = (long)(stat.f_blocks * stat.f_frsize);
                available = (long)(stat.f_bavail * stat.f_frsize);
                used = total - (long)(stat.f_bfree * stat.f_frsize);

                if (total > 0 && available >= 0 && used >= 0)
                    return true;
            }
            catch
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// Identifies the filesystem containing <paramref name="path"/>. Two paths on the same
    /// filesystem report the same id, so callers can avoid showing the same disk twice.
    /// </summary>
    public static bool GetFileSystemId(string path, out ulong fileSystemId)
    {
        fileSystemId = 0;

        if (!Directory.Exists(path) || statvfs(path, out StatVFS stat) != 0)
            return false;

        fileSystemId = stat.f_fsid;
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct StatVFS
    {
        public ulong f_bsize;
        public ulong f_frsize;
        public ulong f_blocks;
        public ulong f_bfree;
        public ulong f_bavail;
        public ulong f_files;
        public ulong f_ffree;
        public ulong f_favail;
        public ulong f_fsid;
        public ulong f_flag;
        public ulong f_namemax;

        // glibc's struct statvfs is 112 bytes; statvfs writes the whole struct, so a shorter
        // declaration overflows the stack and corrupts the caller. musl is shorter, which is fine.
        public uint f_type;
        public int f_spare0;
        public int f_spare1;
        public int f_spare2;
        public int f_spare3;
        public int f_spare4;
    }
}
