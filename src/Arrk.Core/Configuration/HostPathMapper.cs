using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Arrk.Core.Configuration;

/// <summary>
/// Maps paths that are valid <em>inside</em> the Arrk container to the equivalent
/// <em>host</em> paths that the Docker daemon needs when creating bind mounts for the
/// managed service containers.
/// <para>
/// Arrk talks to the Docker daemon over the host socket, so any bind mount source it
/// sends must be a host path. When Arrk itself runs in a container (e.g. the shipped
/// image with <c>-v /mnt/arrk/media:/media</c>), a path like <c>/media/tv</c> exists
/// inside Arrk's container but not on the host, the host path is
/// <c>/mnt/arrk/media/tv</c>. This class inspects Arrk's own container to discover
/// those mount mappings and translates accordingly.
/// </para>
/// <para>
/// Outside a container (standalone) there are no mounts to map and paths are returned
/// unchanged, so a relative data route such as <c>./media</c> works directly.
/// </para>
/// </summary>
public class HostPathMapper
{
    readonly IReadOnlyList<MountMapping> _mounts;

    public HostPathMapper(IReadOnlyList<MountMapping> mounts)
    {
        // Longest destination first so the most specific mount wins.
        _mounts = mounts
            .OrderByDescending(m => m.ContainerPath.Length)
            .ToList();
    }

    /// <summary>A single bind/volume mount: container destination to host source.</summary>
    public readonly record struct MountMapping(string HostPath, string ContainerPath);

    /// <summary>
    /// The mapper for the current process: discovered from Arrk's own container when
    /// running in Docker, otherwise an empty (identity) mapper.
    /// </summary>
    public static HostPathMapper Detect() => new(DetectMounts());

    static readonly Lazy<HostPathMapper> _default = new(Detect);

    /// <summary>
    /// Cached mapper for the current process. Mounts do not change while the process runs,
    /// so discovery is performed once.
    /// </summary>
    public static HostPathMapper Default => _default.Value;

    /// <summary>
    /// Translates a path expressed inside the Arrk container to the host path the Docker
    /// daemon needs. Paths that do not sit under any known mount are returned unchanged.
    /// </summary>
    public string ToHostPath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return path;

        string normalized = Normalize(path);

        foreach (MountMapping mount in _mounts)
        {
            string containerPath = Normalize(mount.ContainerPath);

            if (containerPath == "/")
            {
                // Root mount: everything maps beneath the host root.
                string rootRemainder = normalized.TrimStart('/');
                return Combine(mount.HostPath, rootRemainder);
            }

            if (normalized.Equals(containerPath, StringComparison.Ordinal))
                return mount.HostPath;

            if (normalized.StartsWith(containerPath + "/", StringComparison.Ordinal))
            {
                string remainder = normalized[(containerPath.Length + 1)..];
                return Combine(mount.HostPath, remainder);
            }
        }

        return path;
    }

    static string Normalize(string path)
    {
        // Use '/' separators and drop any trailing slash (except the root) for comparison.
        string p = path.Replace('\\', '/');
        if (p.Length > 1)
            p = p.TrimEnd('/');
        return p.Length == 0 ? "/" : p;
    }

    static string Combine(string hostRoot, string remainder)
    {
        string normalized = hostRoot.Replace('\\', '/').TrimEnd('/');
        // Preserve an absolute leading slash for the root mount ("/").
        if (normalized.Length == 0)
            normalized = "/";

        if (string.IsNullOrEmpty(remainder))
            return normalized == "/" ? "/" : normalized;

        return normalized == "/" ? "/" + remainder : normalized + "/" + remainder;
    }

    static IReadOnlyList<MountMapping> DetectMounts()
    {
        try
        {
            string? containerId = Environment.GetEnvironmentVariable("HOSTNAME");
            if (string.IsNullOrWhiteSpace(containerId))
                return [];

            string socketPath = Environment.GetEnvironmentVariable("DOCKER_SOCKET") ?? "/var/run/docker.sock";

            using var handler = new SocketsHttpHandler();
            handler.ConnectCallback = async (context, cancellationToken) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.IP);
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            };

            using var client = new HttpClient(handler);
            using HttpResponseMessage response = client
                .GetAsync($"http://localhost/containers/{containerId}/json")
                .GetAwaiter().GetResult();

            if (!response.IsSuccessStatusCode)
                return [];

            string json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            using JsonDocument doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("Mounts", out JsonElement mounts) ||
                mounts.ValueKind != JsonValueKind.Array)
                return [];

            List<MountMapping> result = [];
            foreach (JsonElement mount in mounts.EnumerateArray())
            {
                if (!mount.TryGetProperty("Source", out JsonElement source) ||
                    !mount.TryGetProperty("Destination", out JsonElement destination))
                    continue;

                string? hostPath = source.GetString();
                string? containerPath = destination.GetString();
                if (string.IsNullOrWhiteSpace(hostPath) || string.IsNullOrWhiteSpace(containerPath))
                    continue;

                // Only translate filesystem mounts (bind or volume); skip the Docker socket.
                if (containerPath == "/var/run/docker.sock")
                    continue;

                result.Add(new MountMapping(hostPath, containerPath));
            }

            return result;
        }
        catch
        {
            // Not in a container, no socket access, or Docker unavailable, identity mapping.
            return [];
        }
    }
}
