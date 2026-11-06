using Docker.DotNet;
using Docker.DotNet.Models;
using Arrk.Core.Models;

namespace Arrk.Docker;

public class ContainerManager(DockerClient client)
{
    public async Task<List<ContainerInfo>> ListContainers(string? labelFilter = null, CancellationToken ct = default)
    {
        Dictionary<string, IDictionary<string, bool>> filters = new();
        if (!string.IsNullOrEmpty(labelFilter))
        {
            filters["label"] = new Dictionary<string, bool> { [labelFilter] = true };
        }

        return await ListContainersWithFilters(filters, ct);
    }

    public async Task<List<ContainerInfo>> ListContainersByLabels(Dictionary<string, string> labels, CancellationToken ct = default)
    {
        Dictionary<string, IDictionary<string, bool>> filters = new();
        if (labels.Count > 0)
        {
            Dictionary<string, bool> labelFilters = new Dictionary<string, bool>();
            foreach (KeyValuePair<string, string> kvp in labels)
            {
                labelFilters[$"{kvp.Key}={kvp.Value}"] = true;
            }
            filters["label"] = labelFilters;
        }

        return await ListContainersWithFilters(filters, ct);
    }

    async Task<List<ContainerInfo>> ListContainersWithFilters(Dictionary<string, IDictionary<string, bool>> filters, CancellationToken ct)
    {
        ContainersListParameters listParams = new()
        {
            All = true,
            Filters = filters
        };
        IList<ContainerListResponse>? containers = await client.Containers.ListContainersAsync(listParams, ct);

        return containers.Select(c => new ContainerInfo
        {
            Id = c.ID,
            Name = c.Names.FirstOrDefault()?.TrimStart('/') ?? "",
            Image = c.Image,
            State = MapState(c.State, c.Status),
            Status = c.Status,
            CreatedAt = c.Created,
            Labels = c.Labels is { } dict ? new(dict) : new(),
            Ports = c.Ports?.Select(p => $"{p.PrivatePort}/{p.Type}").ToList() ?? [],
            Networks = c.NetworkSettings?.Networks?.Keys.ToList() ?? [],
            Mounts = c.Mounts?
                .Where(m => m.Type is "bind" or "volume")
                .Select(m => new VolumeMount
                {
                    ContainerPath = m.Destination,
                    HostPath = m.Source,
                    Type = m.Type == "bind" ? VolumeType.Bind : VolumeType.NamedVolume,
                    Permissions = m.RW ? VolumePerms.ReadWrite : VolumePerms.ReadOnly
                })
                .ToList() ?? []
        }).ToList();
    }

    public async Task<ContainerInfo?> GetContainer(string name, CancellationToken ct = default)
    {
        List<ContainerInfo> containers = await ListContainers(ct: ct);
        return containers.FirstOrDefault(c => c.Name == name || c.Id.StartsWith(name));
    }

    public async Task<List<ContainerInfo>> GetContainersByImageAsync(string image, CancellationToken ct = default)
    {
        List<ContainerInfo> allContainers = await ListContainers(ct: ct);
        string imageWithoutRef = image.Split(':')[0].Split('@')[0];

        return allContainers.Where(c =>
            c.Image.Equals(image, StringComparison.OrdinalIgnoreCase) ||
            c.Image.Equals(imageWithoutRef, StringComparison.OrdinalIgnoreCase) ||
            c.Image.StartsWith(imageWithoutRef + ":", StringComparison.OrdinalIgnoreCase) ||
            c.Image.StartsWith(imageWithoutRef + "@", StringComparison.OrdinalIgnoreCase)
        ).ToList();
    }

    public async Task<bool> StartContainer(string containerId, CancellationToken ct = default)
    {
        ContainerStartParameters startParams = new();
        return await client.Containers.StartContainerAsync(containerId, startParams, ct);
    }

    public async Task StopContainer(string containerId, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        ContainerStopParameters stopParams = new()
        {
            WaitBeforeKillSeconds = (uint?)timeout?.TotalSeconds ?? 10
        };
        await client.Containers.StopContainerAsync(containerId, stopParams, ct);
    }

    public async Task RestartContainer(string containerId, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        ContainerRestartParameters restartParams = new()
        {
            WaitBeforeKillSeconds = (uint?)timeout?.TotalSeconds ?? 10
        };
        await client.Containers.RestartContainerAsync(containerId, restartParams, ct);
    }

    public async Task RemoveContainer(string containerId, bool force = false, CancellationToken ct = default)
    {
        ContainerRemoveParameters removeParams = new()
        {
            Force = force,
            RemoveVolumes = true
        };
        await client.Containers.RemoveContainerAsync(containerId, removeParams, ct);
    }

    public async Task<ContainerInspectResponse> InspectContainer(string containerId, CancellationToken ct = default)
    {
        return await client.Containers.InspectContainerAsync(containerId, ct);
    }

    /// <summary>
    /// Docker network mode of a container, including the resolved parent ID for
    /// <c>container:&lt;id&gt;</c> modes.
    /// </summary>
    public async Task<string> GetContainerNetworkMode(string containerId, CancellationToken ct = default)
    {
        ContainerInspectResponse inspect = await client.Containers.InspectContainerAsync(containerId, ct);
        return inspect.HostConfig?.NetworkMode ?? "";
    }

    public async Task<string> CreateContainerAsync(CreateContainerParameters parameters, CancellationToken ct = default)
    {
        CreateContainerResponse? response = await client.Containers.CreateContainerAsync(parameters, ct);
        return response.ID;
    }

    public async Task CreateAndStartContainer(CreateContainerParameters parameters, CancellationToken ct = default)
    {
        CreateContainerResponse? response = await client.Containers.CreateContainerAsync(parameters, ct);
        ContainerStartParameters startParams = new();
        await client.Containers.StartContainerAsync(response.ID, startParams, ct);
    }

    public async Task<string> GetContainerLogs(string containerId, int tail = 100, CancellationToken ct = default)
    {
        ContainerLogsParameters parameters = new ContainerLogsParameters
        {
            ShowStdout = true,
            ShowStderr = true,
            Tail = tail.ToString()
        };
#pragma warning disable CS0618
        using Stream? stream = await client.Containers.GetContainerLogsAsync(containerId, parameters, ct);
#pragma warning restore CS0618
        using StreamReader reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(ct);
    }

    public async Task<string> ExecInContainer(string containerId, string[] command, CancellationToken ct = default)
    {
        ContainerExecCreateParameters execParams = new ContainerExecCreateParameters
        {
            AttachStdout = true,
            AttachStderr = true,
            Cmd = command
        };
        ContainerExecCreateResponse? execResponse = await client.Exec.ExecCreateContainerAsync(containerId, execParams, ct);
        using MultiplexedStream? stream = await client.Exec.StartAndAttachContainerExecAsync(execResponse.ID, false);
        (string? stdout, _) = await stream.ReadOutputToEndAsync(ct);
        return stdout;
    }

    /// <summary>
    /// Runs a command in a container with a TTY and feeds it input on stdin. Used for interactive
    /// CLIs that read from a terminal (e.g. <c>navidrome user create</c>, which refuses a pipe).
    /// </summary>
    public async Task<string> ExecWithTtyInput(string containerId, string[] command, string input, CancellationToken ct = default)
    {
        ContainerExecCreateParameters execParams = new ContainerExecCreateParameters
        {
            AttachStdin = true,
            AttachStdout = true,
            AttachStderr = true,
            Tty = true,
            Cmd = command
        };
        ContainerExecCreateResponse? execResponse = await client.Exec.ExecCreateContainerAsync(containerId, execParams, ct);
        using MultiplexedStream stream = await client.Exec.StartAndAttachContainerExecAsync(execResponse.ID, true, ct);

        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(input);
        await stream.WriteAsync(bytes, 0, bytes.Length, ct);

        (string? stdout, _) = await stream.ReadOutputToEndAsync(ct);
        return stdout;
    }

    static Core.Enums.ContainerState MapState(string state, string status)
    {
        return state.ToLowerInvariant() switch
        {
            "created" => Core.Enums.ContainerState.Created,
            "running" => Core.Enums.ContainerState.Running,
            "paused" => Core.Enums.ContainerState.Paused,
            "restarting" => Core.Enums.ContainerState.Restarting,
            "removing" => Core.Enums.ContainerState.Removing,
            "exited" => Core.Enums.ContainerState.Exited,
            "dead" => Core.Enums.ContainerState.Dead,
            _ => Core.Enums.ContainerState.Unknown
        };
    }
}
