using Docker.DotNet;
using Docker.DotNet.Models;

namespace Stackarr.TestSupport;

/// <summary>
/// Scoped Docker cleanup for tests. It only ever removes resources that belong to the current
/// <see cref="TestRun"/>, resources created by a test helper carry the
/// <see cref="TestRun.RunLabel"/>, and resources created through a test <c>StackarrInstance</c>
/// carry an instance id prefixed with <see cref="TestRun.InstancePrefix"/>. Resources from a
/// concurrently executing test process therefore never match and are never touched.
/// </summary>
public static class DockerTestCleanup
{
    /// <summary>
    /// Removes a single container together with the volumes it owns. Named volumes are harvested
    /// from the container's mounts before removal because Docker's <c>RemoveVolumes</c> only
    /// deletes anonymous volumes; named config volumes must be removed explicitly.
    /// </summary>
    public static async Task RemoveOwnedContainerAsync(DockerClient client, string containerId)
    {
        if (string.IsNullOrEmpty(containerId))
            return;

        List<string> volumes = [];
        try
        {
            ContainerInspectResponse inspect = await client.Containers.InspectContainerAsync(containerId);
            foreach (MountPoint mount in inspect.Mounts ?? [])
            {
                if (mount.Type == "volume" && !string.IsNullOrEmpty(mount.Name))
                    volumes.Add(mount.Name);
            }
        }
        catch
        {
            // Container already gone or unreadable, nothing to harvest.
        }

        try
        {
            await client.Containers.RemoveContainerAsync(containerId,
                new ContainerRemoveParameters { Force = true, RemoveVolumes = true });
        }
        catch
        {
            // Already removed or daemon raced us.
        }

        foreach (string volume in volumes)
        {
            try { await client.Volumes.RemoveAsync(volume); }
            catch { /* In use, missing, or not ours. */ }
        }
    }

    /// <summary>
    /// Removes every container (and their named volumes) and network created by this run.
    /// Never matches by bare name prefix, so it cannot affect another concurrent run.
    /// </summary>
    public static async Task RemoveRunAsync(DockerClient? client = null)
    {
        client ??= DockerTestContainer.CreateClient();

        IList<ContainerListResponse> containers;
        try
        {
            containers = await client.Containers.ListContainersAsync(new ContainersListParameters { All = true });
        }
        catch
        {
            return;
        }

        foreach (ContainerListResponse container in containers)
        {
            if (BelongsToRun(container.Labels))
                await RemoveOwnedContainerAsync(client, container.ID);
        }

        IList<NetworkResponse> networks;
        try
        {
            networks = await client.Networks.ListNetworksAsync(new NetworksListParameters());
        }
        catch
        {
            return;
        }

        foreach (NetworkResponse network in networks)
        {
            // Never delete the shared networks (the real instance and the suite-wide test network),
            // nor any network we did not create.
            if (network.Name == "stackarr-net" || network.Name == TestRun.SharedNetworkName || !IsRunNetwork(network.Labels))
                continue;

            try { await client.Networks.DeleteNetworkAsync(network.ID); }
            catch { /* Still in use or already removed. */ }
        }

        // Named volumes for this run are prefixed with the run-scoped container prefix. Remove any
        // that are still around (e.g. orphaned when a container was replaced during a test).
        await RemoveRunVolumesAsync(client);
    }

    static async Task RemoveRunVolumesAsync(DockerClient client)
    {
        string prefix = $"stackarr-{TestRun.InstancePrefix}-";

        IList<VolumeResponse> volumes;
        try
        {
            VolumesListResponse response = await client.Volumes.ListAsync(new VolumesListParameters());
            volumes = response.Volumes ?? [];
        }
        catch
        {
            return;
        }

        foreach (VolumeResponse volume in volumes)
        {
            if (string.IsNullOrEmpty(volume.Name) || !volume.Name.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            try { await client.Volumes.RemoveAsync(volume.Name); }
            catch { /* Still referenced or already gone. */ }
        }
    }

    static bool BelongsToRun(IDictionary<string, string>? labels)
    {
        if (labels is null)
            return false;

        if (labels.TryGetValue(TestRun.RunLabel, out string? runId) && runId == TestRun.Id)
            return true;

        // Containers created through a test StackarrInstance (e.g. by ServiceProvisioner /
        // ConfigSyncer) are identified by their instance id rather than the run label.
        return labels.TryGetValue("stackarr.instance-id", out string? instanceId)
            && instanceId.StartsWith(TestRun.InstancePrefix, StringComparison.Ordinal);
    }

    static bool IsRunNetwork(IDictionary<string, string>? labels) =>
        labels is not null
        && labels.TryGetValue(TestRun.RunLabel, out string? runId)
        && runId == TestRun.Id;
}
