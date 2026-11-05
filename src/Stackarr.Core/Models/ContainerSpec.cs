namespace Stackarr.Core.Models;

public class ContainerSpec
{
    public required string Image { get; init; }
    public required string Name { get; init; }
    public List<VolumeMount> Volumes { get; init; } = [];

    /// <summary>
    /// Extra directories that must exist on the host before the container starts, expressed as
    /// relative-volume keys (e.g. <c>$downloads/slskd</c>). Bind-mount sources are created
    /// automatically; this covers services that expect subdirectories of a mounted root (which
    /// is not itself a mount point) to be present.
    /// </summary>
    public List<string> RequiredDirectories { get; init; } = [];
    public Dictionary<string, string> Environment { get; init; } = new();
    public List<PortMapping> Ports { get; init; } = [];
    public Dictionary<string, string> Labels { get; init; } = new();
    public string NetworkName { get; init; } = "stackarr-net";
    public string? NetworkMode { get; init; }
    public RestartPolicyType RestartPolicy { get; init; } = RestartPolicyType.UnlessStopped;
    public TraefikConfig? Traefik { get; init; }
    public List<string> Capabilities { get; init; } = [];
    public List<string> Devices { get; init; } = [];
    public string? Runtime { get; init; }
    public List<string> Command { get; init; } = [];
    public string? CpuShares { get; init; }
    public long? MemoryLimit { get; init; }
}

public enum VolumePerms
{
    ReadOnly,
    ReadWrite
}

public enum VolumeType
{
    NamedVolume,
    Bind
}

public class VolumeMount
{
    public required string HostPath { get; init; }
    public required string ContainerPath { get; init; }
    public required VolumeType Type { get; init; }
    public VolumePerms Permissions { get; init; } = VolumePerms.ReadOnly;
}

public class PortMapping
{
    public required int HostPort { get; init; }
    public required int ContainerPort { get; init; }
    public string Protocol { get; init; } = "tcp";
}

public class TraefikConfig
{
    public required string ServiceName { get; init; }
    public required string Rule { get; init; }
    public required int Port { get; init; }
    public string EntryPoints { get; init; } = "web";

    /// <summary>
    /// Optional Traefik middlewares to attach to this router (by name). The middleware
    /// definitions themselves must be supplied via <see cref="MiddlewareDefinitions"/>
    /// unless they are provided elsewhere (e.g. the Traefik file provider).
    /// </summary>
    public List<string> Middlewares { get; init; } = [];

    /// <summary>
    /// When set, a <c>stripprefix</c> middleware is generated with this prefix and attached
    /// to the router. Used for upstreams that do not support serving under a URL base.
    /// </summary>
    public string? StripPrefix { get; init; }

    /// <summary>
    /// Raw Traefik label key/value pairs (without the leading <c>traefik.</c>) supporting
    /// advanced per-service configuration such as custom middleware definitions.
    /// </summary>
    public Dictionary<string, string> MiddlewareDefinitions { get; init; } = new();
}

public enum RestartPolicyType
{
    No,
    Always,
    UnlessStopped,
    OnFailure
}
