using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Arrk.Core;
using Arrk.Core.Configuration;
using Arrk.Core.Models;
using Arrk.Core.Services;
using Arrk.Docker;

namespace Arrk.Orchestration;

using DockerModels = global::Docker.DotNet.Models;

public class ServiceProvisioner(
    ContainerManager containerManager,
    NetworkManager networkManager,
    ImageManager imageManager,
    ResourceInventory inventory,
    IOptionsMonitor<ArrkConfig> configMonitor,
    ArrkInstance arrk,
    LiveActivityRegistry liveActivities,
    ILogger<ServiceProvisioner> logger)
{
    readonly string _instanceId = arrk.StackName;

    /// <summary>Label carrying a fingerprint of the desired spec so drift can be detected.</summary>
    public const string SpecHashLabel = "stackarr.spec-hash";

    const int MaxRetries = 3;
    static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    public async Task<string> ProvisionContainer(ContainerSpec spec, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentException.ThrowIfNullOrWhiteSpace(spec.Image);
        ArgumentException.ThrowIfNullOrWhiteSpace(spec.Name);

        logger.LogInformation("Provisioning container {Name} with image {Image}", spec.Name, spec.Image);

        bool sharedNetwork = UsesSharedNetwork(spec);
        ContainerInfo? existing = await containerManager.GetContainer(spec.Name, ct);
        bool recreating = false;
        if (existing != null)
        {
            // A VPN-routed container stores the parent's container ID, not its name. When
            // Gluetun is recreated (config/image change) the dependent still points at the
            // removed ID and can never start, even though its own labels are unchanged.
            if (await SharesStaleParentAsync(spec, existing, ct))
            {
                logger.LogInformation(
                    "Container {Name} shares the network namespace of a recreated parent, recreating it",
                    spec.Name);
                recreating = true;
            }
            else if (!LabelsDiffer(existing.Labels, BuildLabels(spec)))
            {
                if (existing.State == Core.Enums.ContainerState.Running)
                {
                    logger.LogDebug("Container {Name} already running with current spec, skipping creation", spec.Name);
                    return existing.Id;
                }

                logger.LogInformation("Container {Name} matches spec but is {State}, starting it", spec.Name, existing.State);
                using LiveActivityScope start = liveActivities.Begin(
                    LiveActivityKind.Container, $"Starting {spec.Name}", "Container was stopped; starting it");
                await containerManager.StartContainer(existing.Id, ct);
                return existing.Id;
            }
            else
            {
                logger.LogInformation(
                    "Container {Name} exists in {State} state with stale configuration, removing before recreation",
                    spec.Name, existing.State);
                recreating = true;
            }
        }

        // From here on we are doing real work: starting/recreating the container (and possibly pulling
        // its image). Surface it as a live activity so it isn't only visible on the Docker page.
        using LiveActivityScope activity = liveActivities.Begin(
            LiveActivityKind.Container,
            recreating ? $"Recreating {spec.Name}" : $"Starting {spec.Name}",
            recreating ? "Configuration changed; rebuilding the container" : $"Pulling image {spec.Image}");

        if (existing != null)
            await containerManager.RemoveContainer(existing.Id, force: true, ct);

        string? networkId = null;
        if (!sharedNetwork)
            networkId = await networkManager.CreateOrGetNetwork(spec.NetworkName, labels: null, ct);

        await PullImageWithRetry(spec.Image, ct);
        activity.Update(recreating ? "Starting the replacement container" : $"Starting {spec.Image}");

        string containerImage = spec.Image;
        if (configMonitor.CurrentValue.PinImagesToDigest)
        {
            string? digest = await imageManager.ResolveDigestAsync(spec.Image, ct);
            if (digest != null)
            {
                containerImage = $"{spec.Image.Split(':')[0]}@{digest}";
                logger.LogInformation("Resolved {Image} to digest-pinned {Pinned}", spec.Image, containerImage);
            }
        }

        return await CreateAndStartContainer(spec, containerImage, networkId, ct);
    }

    /// <summary>
    /// True when the container shares a parent container's network namespace and the stored
    /// parent ID no longer matches the running parent (the parent was recreated).
    /// </summary>
    async Task<bool> SharesStaleParentAsync(ContainerSpec spec, ContainerInfo existing, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(spec.NetworkMode)
            || !spec.NetworkMode.StartsWith("container:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string parentName = spec.NetworkMode["container:".Length..];
        ContainerInfo? parent = await containerManager.GetContainer(parentName, ct);
        if (parent is null)
            return false;

        string expected = $"container:{parent.Id}";
        string actual = await containerManager.GetContainerNetworkMode(existing.Id, ct);
        return !string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when the container does not join a Docker network itself, it shares another
    /// container's network namespace (VPN sidecar), uses the host, or is network-less.
    /// Docker rejects a create that also specifies a networking config in these modes.
    /// </summary>
    public static bool UsesSharedNetwork(ContainerSpec spec) =>
        !string.IsNullOrWhiteSpace(spec.NetworkMode)
        && (spec.NetworkMode!.StartsWith("service:", StringComparison.OrdinalIgnoreCase)
            || spec.NetworkMode.StartsWith("container:", StringComparison.OrdinalIgnoreCase)
            || spec.NetworkMode.Equals("host", StringComparison.OrdinalIgnoreCase)
            || spec.NetworkMode.Equals("none", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whether the existing container already matches the desired spec (running and with
    /// identical labels, which include the spec fingerprint).
    /// </summary>
    public bool MatchesSpec(ContainerSpec spec, ContainerInfo existing) =>
        existing.State == Core.Enums.ContainerState.Running
        && !LabelsDiffer(existing.Labels, BuildLabels(spec));

    public async Task<bool> EnsureContainerRunning(string containerName, CancellationToken ct = default)
    {
        ContainerInfo? container = await containerManager.GetContainer(containerName, ct);
        if (container == null)
            return false;
        if (container.State == Core.Enums.ContainerState.Running)
            return true;

        logger.LogInformation("Restarting stopped container {Name}", containerName);
        using LiveActivityScope activity = liveActivities.Begin(
            LiveActivityKind.Container, $"Starting {containerName}", "Container was stopped; starting it");
        await containerManager.RestartContainer(container.Id, ct: ct);
        return true;
    }

    /// <summary>
    /// Force-restarts a container even if it is already running, used to reset a service whose
    /// internal state is stuck (e.g. a Soulseek reconnect backoff). Not the same as recovery, which
    /// only starts a stopped container.
    /// </summary>
    public async Task<bool> RestartContainer(string containerName, CancellationToken ct = default)
    {
        ContainerInfo? container = await containerManager.GetContainer(containerName, ct);
        if (container == null)
        {
            logger.LogWarning("Container {Name} not found, cannot restart", containerName);
            return false;
        }

        logger.LogInformation("Force-restarting container {Name}", containerName);
        using LiveActivityScope activity = liveActivities.Begin(
            LiveActivityKind.Container, $"Restarting {containerName}", "Restarting the container");
        await containerManager.RestartContainer(container.Id, ct: ct);
        return true;
    }

    public async Task StopContainer(string containerName, CancellationToken ct = default)
    {
        ContainerInfo? container = await containerManager.GetContainer(containerName, ct);
        if (container == null)
        {
            logger.LogDebug("Container {Name} not found, nothing to stop", containerName);
            return;
        }

        if (container.State is Core.Enums.ContainerState.Exited
            or Core.Enums.ContainerState.Dead
            or Core.Enums.ContainerState.Removing)
        {
            logger.LogDebug("Container {Name} already stopped (state: {State})", containerName, container.State);
            return;
        }

        logger.LogInformation("Stopping container {Name}", containerName);
        using LiveActivityScope activity = liveActivities.Begin(
            LiveActivityKind.Container, $"Stopping {containerName}", "Stopping the container");
        await containerManager.StopContainer(container.Id, ct: ct);
    }

    public async Task RemoveContainer(string containerName, CancellationToken ct = default)
    {
        ContainerInfo? container = await containerManager.GetContainer(containerName, ct);
        if (container == null)
        {
            logger.LogDebug("Container {Name} not found, nothing to remove", containerName);
            return;
        }

        logger.LogInformation("Removing container {Name}", containerName);
        await containerManager.RemoveContainer(container.Id, force: true, ct: ct);
    }

    public async Task<string> GetContainerLogs(string containerName, int tail = 100, CancellationToken ct = default)
    {
        ContainerInfo? container = await containerManager.GetContainer(containerName, ct);
        if (container == null)
        {
            logger.LogWarning("Cannot get logs: container {Name} not found", containerName);
            return string.Empty;
        }

        return await containerManager.GetContainerLogs(container.Id, tail, ct);
    }

    public async Task<ContainerInfo?> GetContainerStatus(string containerName, CancellationToken ct = default)
    {
        return await containerManager.GetContainer(containerName, ct);
    }

    public async Task<List<ContainerInfo>> GetAllManagedContainers(CancellationToken ct = default)
    {
        return await inventory.GetOwnedContainers(ct);
    }

    public async Task<int> GetRunningContainerCount(CancellationToken ct = default)
    {
        List<ContainerInfo> owned = await inventory.GetOwnedContainers(ct);
        return owned.Count(c => c.State == Core.Enums.ContainerState.Running);
    }

    public async Task<List<ContainerInfo>> GetContainersByService(string serviceName, CancellationToken ct = default)
    {
        return await inventory.GetOwnedContainersByService(serviceName, ct);
    }

    async Task<string> CreateAndStartContainer(ContainerSpec spec, string containerImage, string networkId, CancellationToken ct)
    {
        await EnsureHostDirectoriesExist(spec, ct);

        DockerModels.CreateContainerParameters parameters = BuildCreateParameters(spec, containerImage, networkId);

        logger.LogInformation("Creating container '{Name}'...", spec.Name);

        string containerId = await containerManager.CreateContainerAsync(parameters, ct);

        logger.LogInformation("Starting container '{Name}' (ID: {Id})",
            spec.Name,
            containerId[..Math.Min(12, containerId.Length)]);

        await containerManager.StartContainer(containerId, ct);

        logger.LogInformation("Container '{Name}' created and started (ID: {Id})",
            spec.Name,
            containerId[..Math.Min(12, containerId.Length)]);

        return containerId;
    }

    async Task EnsureHostDirectoriesExist(ContainerSpec spec, CancellationToken ct)
    {
        VolumePathResolver volumeResolver = new(configMonitor.CurrentValue.Storage);

        foreach (VolumeMount mount in spec.Volumes)
        {
            if (string.IsNullOrEmpty(mount.HostPath))
                continue;

            if (mount.Type != VolumeType.Bind)
                continue;

            if (volumeResolver.ResolveToHostPath(mount.HostPath) == "/var/run/docker.sock")
                continue;

            await EnsureDirectoryExists(volumeResolver, mount.HostPath, ct);
        }

        // Directories inside a mounted root that a service expects to already exist (they are
        // not mount points themselves, so the loop above does not create them).
        foreach (string directory in spec.RequiredDirectories)
            await EnsureDirectoryExists(volumeResolver, directory, ct);
    }

    async Task EnsureDirectoryExists(VolumePathResolver volumeResolver, string volumeKey, CancellationToken ct)
    {
        string containerPath = volumeResolver.ResolveToContainerPath(volumeKey);
        string hostPath = volumeResolver.ResolveToHostPath(volumeKey);
        // The path to create from inside this process: data-route anchored and, in a
        // container, reachable through Arrk's own mount (so it is created on the
        // host). This differs from hostPath, which is the host-side path Docker needs.
        string localPath = volumeResolver.ResolveToLocalPath(volumeKey);

        // Create the bind source so Docker can mount it. In a container this writes
        // through Arrk's mount to the host; standalone it is the local path.
        try
        {
            logger.LogInformation("Creating directory {LocalPath} (host: '{HostPath}') to mount at '{ContainerPath}'",
                localPath,
                hostPath,
                containerPath);

            Directory.CreateDirectory(localPath);

            using Process chmodProcess = Process.Start(new ProcessStartInfo
            {
                FileName = "chmod",
                Arguments = $"-R ug+rw \"{localPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            })!;

            chmodProcess.OutputDataReceived += (s, e) => logger.LogInformation(
                "Shell output: {Data}", e.Data);
            chmodProcess.ErrorDataReceived += (s, e) => logger.LogInformation(
                "Shell error output: {Data}", e.Data);
            chmodProcess.BeginOutputReadLine();

            await chmodProcess.WaitForExitAsync(ct);

            using Process chownProcess = Process.Start(new ProcessStartInfo
            {
                FileName = "chown",
                Arguments = $"-R 1000:1000 \"{localPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            })!;

            chownProcess.OutputDataReceived += (s, e) => logger.LogInformation(
                "Shell output: {Data}", e.Data);
            chownProcess.ErrorDataReceived += (s, e) => logger.LogInformation(
                "Shell error output: {Data}", e.Data);
            chownProcess.BeginOutputReadLine();

            await chownProcess.WaitForExitAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create directory under '{ContainerPath}' (local: '{LocalPath}', host: '{HostPath}')",
                containerPath,
                localPath,
                hostPath);
        }
    }

    async Task PullImageWithRetry(string image, CancellationToken ct)
    {
        for (int attempt = 0; attempt < MaxRetries; attempt++)
        {
            try
            {
                logger.LogInformation("Pulling image {Image}...", image);
                await imageManager.PullImage(image, ct);
                logger.LogInformation("Pulled image {Image}", image);
                return;
            }
            catch (Exception ex) when (attempt < MaxRetries - 1)
            {
                logger.LogWarning(ex, "Failed to pull image {Image} (attempt {Attempt}/{MaxRetries}), retrying in {Delay}s",
                    image,
                    attempt + 1,
                    MaxRetries,
                    RetryDelay.TotalSeconds);

                await Task.Delay(RetryDelay, ct);
            }
        }
    }

    Dictionary<string, string> BuildLabels(ContainerSpec spec)
    {
        Dictionary<string, string> allLabels = new(spec.Labels)
        {
            [ContainerLabels.Managed] = "true",
            [ContainerLabels.InstanceId] = _instanceId,
            [ContainerLabels.Network] = spec.NetworkName,
            [SpecHashLabel] = ComputeSpecHash(spec)
        };

        // A container sharing another container's network namespace (VPN sidecar) cannot be
        // discovered by Traefik's Docker provider, so it gets no Docker labels. Its routes
        // are written to Traefik's file provider instead (pointing at Gluetun).
        if (spec.Traefik != null && !UsesSharedNetwork(spec))
        {
            string router = spec.Traefik.ServiceName;

            // Traefik should resolve the container on Arrk's network when it is attached
            // to more than one (e.g. VPN-routed containers share Gluetun's namespace).
            allLabels["traefik.docker.network"] = spec.NetworkName;

            allLabels["traefik.enable"] = "true";
            allLabels[$"traefik.http.routers.{router}.rule"] = spec.Traefik.Rule;
            allLabels[$"traefik.http.services.{router}.loadbalancer.server.port"] =
                spec.Traefik.Port.ToString();
            allLabels[$"traefik.http.routers.{router}.entrypoints"] = spec.Traefik.EntryPoints;

            List<string> middlewares = [.. spec.Traefik.Middlewares];
            if (!string.IsNullOrWhiteSpace(spec.Traefik.StripPrefix))
            {
                string middlewareName = $"{router}-stripprefix";
                allLabels[$"traefik.http.middlewares.{middlewareName}.stripprefix.prefixes"] =
                    spec.Traefik.StripPrefix;
                middlewares.Insert(0, middlewareName);
            }

            foreach ((string key, string value) in spec.Traefik.MiddlewareDefinitions)
            {
                allLabels[$"traefik.http.middlewares.{key}"] = value;
            }

            if (middlewares.Count > 0)
            {
                allLabels[$"traefik.http.routers.{router}.middlewares"] = string.Join(",", middlewares);
            }
        }

        return allLabels;
    }

    /// <summary>
    /// Stable fingerprint of everything that affects how a container is created. Stored as
    /// a label so a running container can be compared cheaply against the desired spec.
    /// Secrets are hashed, not stored in the label.
    /// </summary>
    public static string ComputeSpecHash(ContainerSpec spec)
    {
        StringBuilder sb = new();
        sb.Append("image=").Append(spec.Image).Append('\n');
        sb.Append("netmode=").Append(spec.NetworkMode ?? "").Append('\n');
        sb.Append("net=").Append(spec.NetworkName).Append('\n');
        sb.Append("runtime=").Append(spec.Runtime ?? "").Append('\n');
        sb.Append("restart=").Append(spec.RestartPolicy).Append('\n');

        foreach (KeyValuePair<string, string> e in spec.Environment.OrderBy(e => e.Key, StringComparer.Ordinal))
            sb.Append("env.").Append(e.Key).Append('=').Append(e.Value).Append('\n');

        foreach (VolumeMount v in spec.Volumes)
            sb.Append("vol.").Append(v.Type).Append('|').Append(v.HostPath).Append('|')
              .Append(v.ContainerPath).Append('|').Append(v.Permissions).Append('\n');

        foreach (string directory in spec.RequiredDirectories)
            sb.Append("dir.").Append(directory).Append('\n');

        foreach (PortMapping p in spec.Ports)
            sb.Append("port.").Append(p.HostPort).Append('|').Append(p.ContainerPort).Append('|').Append(p.Protocol).Append('\n');

        foreach (string c in spec.Capabilities) sb.Append("cap.").Append(c).Append('\n');
        foreach (string d in spec.Devices) sb.Append("dev.").Append(d).Append('\n');
        foreach (string c in spec.Command) sb.Append("cmd.").Append(c).Append('\n');

        if (spec.Traefik != null)
        {
            sb.Append("traefik=").Append(spec.Traefik.ServiceName).Append('|')
              .Append(spec.Traefik.Rule).Append('|').Append(spec.Traefik.Port).Append('|')
              .Append(spec.Traefik.EntryPoints).Append('|').Append(spec.Traefik.StripPrefix ?? "").Append('\n');
            foreach (string m in spec.Traefik.Middlewares) sb.Append("tw.").Append(m).Append('\n');
            foreach (KeyValuePair<string, string> m in spec.Traefik.MiddlewareDefinitions.OrderBy(m => m.Key, StringComparer.Ordinal))
                sb.Append("tm.").Append(m.Key).Append('=').Append(m.Value).Append('\n');
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash)[..16];
    }

    /// <summary>
    /// Splits a device spec in the <c>host-path[:container-path]</c> form that service
    /// definitions use into the two paths Docker.DotNet expects. When no container path is
    /// given, the host path is used on both sides.
    /// </summary>
    public static (string HostPath, string ContainerPath) ParseDevice(string device)
    {
        string[] parts = device.Split(':', 2);
        string host = parts[0];
        string container = parts.Length > 1 && parts[1].Length > 0 ? parts[1] : parts[0];
        return (host, container);
    }

    // A running container is reused only when its labels still match the desired state. This
    // is what lets a config change (e.g. toggling a service's `proxy` flag or VPN routing)
    // take effect: the stale container is recreated on the next provisioning pass. The
    // spec fingerprint label makes this cover non-label fields too (env, network mode, ...).
    static bool LabelsDiffer(IReadOnlyDictionary<string, string> existing, IReadOnlyDictionary<string, string> desired)
    {
        foreach ((string key, string value) in desired)
        {
            if (!existing.TryGetValue(key, out string? current) || current != value)
                return true;
        }

        foreach (string key in existing.Keys)
        {
            if (key.StartsWith("traefik.", StringComparison.Ordinal) && !desired.ContainsKey(key))
                return true;
        }

        return false;
    }

    DockerModels.CreateContainerParameters BuildCreateParameters(ContainerSpec spec, string containerImage, string networkId)
    {
        Dictionary<string, string> allLabels = BuildLabels(spec);

        DockerModels.HostConfig hostConfig = new()
        {
            Mounts = BuildMounts(spec.Volumes),
            // A container sharing another's network namespace cannot publish ports itself; the
            // namespace owner (Gluetun) publishes them instead.
            PortBindings = UsesSharedNetwork(spec) ? null : BuildPortBindings(spec.Ports),
            RestartPolicy = MapRestartPolicy(spec.RestartPolicy),
            NetworkMode = spec.NetworkMode
        };

        if (spec.Capabilities.Count > 0)
        {
            hostConfig.CapAdd = spec.Capabilities;
        }

        if (spec.Devices.Count > 0)
        {
            hostConfig.Devices = spec.Devices.Select(d =>
            {
                (string host, string container) = ParseDevice(d);
                return new DockerModels.DeviceMapping
                {
                    PathOnHost = host,
                    PathInContainer = container,
                    CgroupPermissions = "rwm"
                };
            }).ToList();
        }

        if (spec.Runtime != null)
        {
            hostConfig.Runtime = spec.Runtime;
        }

        DockerModels.CreateContainerParameters createParams = new()
        {
            Image = containerImage,
            Name = spec.Name,
            Labels = allLabels,
            Env = spec.Environment.Count > 0
                ? spec.Environment.Select(e => $"{e.Key}={e.Value}").ToList()
                : null,
            HostConfig = hostConfig
        };

        if (spec.Command.Count > 0)
        {
            createParams.Cmd = spec.Command;
        }

        if (networkId != null)
        {
            createParams.NetworkingConfig = new()
            {
                EndpointsConfig = new Dictionary<string, DockerModels.EndpointSettings>
                {
                    [networkId] = new()
                }
            };
        }

        return createParams;
    }

    List<DockerModels.Mount> BuildMounts(List<VolumeMount> volumes)
    {
        VolumePathResolver volumeResolver = new(configMonitor.CurrentValue.Storage);

        return volumes.Select(v =>
        {
            return v.Type switch
            {
                VolumeType.NamedVolume => new DockerModels.Mount
                {
                    Type = "volume",
                    Source = v.HostPath,
                    Target = v.ContainerPath,
                    ReadOnly = v.Permissions == VolumePerms.ReadOnly
                },
                VolumeType.Bind => new DockerModels.Mount
                {
                    Type = "bind",
                    Source = volumeResolver.ResolveToHostPath(v.HostPath),
                    Target = v.ContainerPath,
                    ReadOnly = v.Permissions == VolumePerms.ReadOnly
                },
                _ => throw new ArgumentOutOfRangeException()
            };
        }).ToList();
    }

    static Dictionary<string, IList<DockerModels.PortBinding>>? BuildPortBindings(List<PortMapping> ports)
    {
        if (ports.Count == 0)
            return null;

        Dictionary<string, IList<DockerModels.PortBinding>> bindings = new();
        foreach (PortMapping port in ports)
        {
            string key = $"{port.ContainerPort}/{port.Protocol}";
            bindings[key] = [new() { HostPort = port.HostPort.ToString() }];
        }
        return bindings;
    }

    static DockerModels.RestartPolicy MapRestartPolicy(RestartPolicyType policy)
    {
        return policy switch
        {
            RestartPolicyType.No => new() { Name = DockerModels.RestartPolicyKind.No },
            RestartPolicyType.Always => new() { Name = DockerModels.RestartPolicyKind.Always },
            RestartPolicyType.UnlessStopped => new() { Name = DockerModels.RestartPolicyKind.UnlessStopped },
            RestartPolicyType.OnFailure => new() { Name = DockerModels.RestartPolicyKind.OnFailure },
            _ => new() { Name = DockerModels.RestartPolicyKind.UnlessStopped }
        };
    }
}
