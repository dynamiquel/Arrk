using Microsoft.Extensions.Logging;
using Stackarr.Core;
using Stackarr.Core.Models;
using Stackarr.Docker;

using DockerClient = Docker.DotNet.DockerClient;
using DockerNet = Docker.DotNet.Models;

namespace Stackarr.Orchestration;

public class ResourceInventory(
    ContainerManager containerManager,
    NetworkManager networkManager,
    DockerMetricsCollector metricsCollector,
    DockerClient dockerClient,
    StackarrInstance stackarr,
    ILogger<ResourceInventory> logger)
{
    readonly DockerMetricsCollector _metricsCollector = metricsCollector;
    readonly string _instanceId = stackarr.StackName;

    public string InstanceId => _instanceId;

    public async Task<List<ContainerInfo>> GetOwnedContainers(CancellationToken ct = default)
    {
        List<ContainerInfo> managed = await containerManager.ListContainers(labelFilter: ContainerLabels.Managed, ct: ct);
        return managed.Where(c =>
            c.Labels.TryGetValue(ContainerLabels.InstanceId, out string? id) && id == _instanceId
        ).ToList();
    }

    public async Task<List<ContainerInfo>> GetAllManagedContainers(CancellationToken ct = default)
    {
        return await containerManager.ListContainers(labelFilter: ContainerLabels.Managed, ct: ct);
    }

    public async Task<List<ContainerInfo>> GetOwnedContainersByService(string serviceName, CancellationToken ct = default)
    {
        List<ContainerInfo> owned = await GetOwnedContainers(ct);
        return owned.Where(c =>
            c.Labels.TryGetValue(ContainerLabels.Service, out string? svc) && svc == serviceName
        ).ToList();
    }

    public async Task<List<string>> GetOwnedNetworkIds(CancellationToken ct = default)
    {
        List<DockerNet.NetworkResponse> networks = await networkManager.ListNetworks(ct);
        return networks
            .Where(n => n.Labels != null && n.Labels.TryGetValue(ContainerLabels.Managed, out string? managed) && managed == "true")
            .Select(n => n.ID)
            .ToList();
    }

    public async Task<bool> IsOwnedContainer(string containerId, CancellationToken ct = default)
    {
        List<ContainerInfo> containers = await containerManager.ListContainers(labelFilter: ContainerLabels.Managed, ct: ct);
        return containers.Any(c =>
            (c.Id == containerId || c.Id.StartsWith(containerId)) &&
            c.Labels.TryGetValue(ContainerLabels.InstanceId, out string? id) && id == _instanceId);
    }

    public async Task<int> GetOwnedContainerCount(CancellationToken ct = default)
    {
        List<ContainerInfo> owned = await GetOwnedContainers(ct);
        return owned.Count;
    }

    public async Task<Dictionary<string, int>> GetOwnedContainersByServiceCount(CancellationToken ct = default)
    {
        List<ContainerInfo> owned = await GetOwnedContainers(ct);
        return owned
            .GroupBy(c => c.Labels.TryGetValue(ContainerLabels.Service, out string? svc) ? svc : "unknown")
            .ToDictionary(g => g.Key, g => g.Count());
    }

    public async Task<List<DockerNet.NetworkResponse>> GetAllNetworks(CancellationToken ct = default)
    {
        return await networkManager.ListNetworks(ct);
    }

    public async Task<List<DockerNet.NetworkResponse>> GetOwnedNetworks(CancellationToken ct = default)
    {
        List<DockerNet.NetworkResponse> all = await networkManager.ListNetworks(ct);
        return all.Where(n =>
            (n.Labels != null && n.Labels.TryGetValue(ContainerLabels.Managed, out string? m) && m == ContainerLabels.ManagedValue)
            || (n.Name != null && n.Name.StartsWith(stackarr.ContainerPrefix, StringComparison.Ordinal))
        ).ToList();
    }

    public async Task<List<VolumeMetrics>> GetOwnedVolumes(CancellationToken ct = default)
    {
        List<VolumeMetrics> volumes = new();

        try
        {
            List<ContainerInfo> managedContainers = await containerManager.ListContainers(
                labelFilter: ContainerLabels.Managed, ct: ct);

            HashSet<string> managedContainerNames = managedContainers
                .Select(c => c.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            Dictionary<string, List<string>> volumeToContainers = new(StringComparer.OrdinalIgnoreCase);

            foreach (ContainerInfo container in managedContainers)
            {
                try
                {
                    DockerNet.ContainerInspectResponse? inspect = await containerManager.InspectContainer(container.Id, ct);
                    if (inspect?.Mounts == null)
                        continue;

                    foreach (DockerNet.MountPoint? mount in inspect.Mounts)
                    {
                        string? key = mount.Type == "bind" ? mount.Source : mount.Name;
                        if (string.IsNullOrEmpty(key))
                            continue;

                        if (!volumeToContainers.TryGetValue(key, out List<string>? list))
                        {
                            list = [];
                            volumeToContainers[key] = list;
                        }
                        list.Add(container.Name);
                    }
                }
                catch { }
            }

            foreach ((string key, List<string> containerNames) in volumeToContainers)
            {
                string mountPoint = "";
                long totalBytes = 0L;
                long usedBytes = 0L;
                long availableBytes = 0L;
                double usedPercent = 0;

                if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Linux))
                {
                    string hostPath = key;
                    try
                    {
                        if (DockerMetricsCollector.GetDiskSpace(hostPath, out long total, out long avail, out long used))
                        {
                            totalBytes = total;
                            availableBytes = avail;
                            usedBytes = used;
                            usedPercent = total > 0 ? (double)used / total * 100 : 0;
                            mountPoint = hostPath;
                        }
                    }
                    catch { }
                }

                volumes.Add(new()
                {
                    Name = key,
                    MountPoint = mountPoint,
                    TotalBytes = totalBytes,
                    UsedBytes = usedBytes,
                    AvailableBytes = availableBytes,
                    UsedPercent = usedPercent,
                    Containers = containerNames
                });
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get owned volumes");
        }

        return volumes;
    }

    public async Task<List<ContainerDiskUsage>> GetContainerDiskUsage(CancellationToken ct = default)
    {
        List<ContainerDiskUsage> results = new();

        try
        {
            List<ContainerInfo> managedContainers = await containerManager.ListContainers(
                labelFilter: ContainerLabels.Managed, ct: ct);

            foreach (ContainerInfo container in managedContainers)
            {
                try
                {
                    DockerNet.ContainerInspectResponse inspect = await containerManager.InspectContainer(container.Id, ct);
                    string serviceName = container.Labels.TryGetValue(ContainerLabels.Service, out string? svc) ? svc : "unknown";

                    long writableBytes = inspect.SizeRw ?? 0;

                    results.Add(new()
                    {
                        ContainerName = container.Name,
                        ServiceName = serviceName,
                        WritableLayerBytes = writableBytes
                    });
                }
                catch { }
            }
        }
        catch { }

        return results;
    }

    public async Task<List<ContainerInfo>> GetContainersOnNetwork(string networkId, CancellationToken ct = default)
    {
        List<ContainerInfo> allContainers = await containerManager.ListContainers(ct: ct);
        return allContainers.Where(c =>
            c.Networks.Contains(networkId) ||
            c.Labels.TryGetValue(ContainerLabels.Network, out string? net) && net == networkId
        ).ToList();
    }

    public async Task<Dictionary<string, List<ContainerInfo>>> GetContainersGroupedByService(CancellationToken ct = default)
    {
        List<ContainerInfo> managed = await containerManager.ListContainers(labelFilter: ContainerLabels.Managed, ct: ct);
        return managed
            .GroupBy(c => c.Labels.TryGetValue(ContainerLabels.Service, out string? svc) ? svc : "unknown")
            .ToDictionary(g => g.Key, g => g.ToList());
    }

    public async Task CleanupOrphanedResources(CancellationToken ct = default)
    {
        logger.LogInformation("Cleaning up orphaned Stackarr resources...");

        // Only ever touch resources owned by this instance. Containers belonging to a
        // different Stackarr instance must never be removed, another instance may be
        // running on the same Docker host and manages its own lifecycle.
        List<ContainerInfo> containers = await containerManager.ListContainers(labelFilter: ContainerLabels.Managed, ct: ct);

        List<ContainerInfo> orphans = containers.Where(c =>
            c.Labels.TryGetValue(ContainerLabels.InstanceId, out string? id) && id == _instanceId
        ).ToList();

        // Without the desired-service set there is no safe way to tell which owned containers are
        // stale, so orphans are left running.
        _ = orphans;

        HashSet<string> ownedContainerIds = (await GetOwnedContainers(ct)).Select(c => c.Id).ToHashSet();

        List<DockerNet.NetworkResponse> allNetworks = await networkManager.ListNetworks(ct);
        List<DockerNet.NetworkResponse> orphanedNetworks = allNetworks.Where(n =>
            OwnsNetwork(n) &&
            !(n.Containers?.Any(kvp => ownedContainerIds.Contains(kvp.Key)) ?? false)
        ).ToList();

        foreach (DockerNet.NetworkResponse network in orphanedNetworks)
        {
            logger.LogWarning(
                "Removing orphaned network {Name} ({Id})",
                network.Name, network.ID[..Math.Min(12, network.ID.Length)]);
            try
            {
                await networkManager.DeleteNetwork(network.ID, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to remove orphaned network {Name}", network.Name);
            }
        }

        logger.LogInformation("Orphan cleanup complete. Removed {ContainerCount} containers, {NetworkCount} networks.",
            0, orphanedNetworks.Count);
    }

    bool OwnsNetwork(DockerNet.NetworkResponse network)
    {
        if (network.Labels == null)
            return false;

        // A network is owned by this instance when it carries our managed label and either
        // our instance id or the instance-scoped name prefix.
        bool managed = network.Labels.TryGetValue(ContainerLabels.Managed, out string? m) && m == ContainerLabels.ManagedValue;
        if (!managed)
            return false;

        if (network.Labels.TryGetValue(ContainerLabels.InstanceId, out string? id))
            return id == _instanceId;

        return network.Name != null && network.Name.StartsWith(stackarr.ContainerPrefix, StringComparison.Ordinal);
    }

    public async Task<List<DockerNet.ImagesListResponse>> GetOwnedImages(CancellationToken ct = default)
    {
        List<ContainerInfo> owned = await GetOwnedContainers(ct);
        List<string> ownedImageRefs = owned.Select(c => c.Image)
            .Where(i => !string.IsNullOrEmpty(i))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        try
        {
            using DockerClient client = DockerClientFactory.Create();
            IList<DockerNet.ImagesListResponse>? allImages = await client.Images.ListImagesAsync(new(), ct);
            return allImages
                .Where(i => i.RepoTags != null &&
                            i.RepoTags.Any(t => !t.Contains("<none>")) &&
                            ImageMatchesOwnedContainer(i, ownedImageRefs))
                .ToList();
        }
        catch { return []; }
    }

    static bool ImageMatchesOwnedContainer(DockerNet.ImagesListResponse image, List<string> ownedImageRefs)
    {
        foreach (string imageRef in ownedImageRefs)
        {
            if (image.RepoTags != null && image.RepoTags.Contains(imageRef, StringComparer.OrdinalIgnoreCase))
                return true;

            if (image.RepoDigests != null && image.RepoDigests.Contains(imageRef, StringComparer.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public async Task<DockerNet.NetworkResponse?> GetNetworkById(string networkId, CancellationToken ct = default)
    {
        try
        {
            List<DockerNet.NetworkResponse> networks = await networkManager.ListNetworks(ct);
            return networks.FirstOrDefault(n =>
                n.ID == networkId || n.ID.StartsWith(networkId, StringComparison.OrdinalIgnoreCase));
        }
        catch { return null; }
    }

    public async Task<DockerNet.VolumeResponse?> GetVolumeByName(string volumeName, CancellationToken ct = default)
    {
        try
        {
            DockerNet.VolumesListResponse? response = await dockerClient.Volumes.ListAsync(new(), ct);
            return response.Volumes?.FirstOrDefault(v =>
                string.Equals(v.Name, volumeName, StringComparison.OrdinalIgnoreCase));
        }
        catch { return null; }
    }
}

public class ContainerDiskUsage
{
    public string ContainerName { get; set; } = "";
    public string ServiceName { get; set; } = "";
    public long WritableLayerBytes { get; set; }
}
