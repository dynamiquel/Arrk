using Stackarr.Core.Configuration;
using Stackarr.Core.Models;

namespace Stackarr.Core.Services;

public static class RelativeVolumes
{
    public const string Config = "$config";
    public const string Downloads = "$downloads";
    public const string Media = "$media";
    public const string DockerSocket = "$docker-socket";
    public const string Tv = "$tv";
    public const string TvMovies = "$tv:movies";
    public const string TvShows = "$tv:shows";
    public const string Music = "$music";

    /// <summary>Directory holding Traefik's dynamic (file provider) configuration.</summary>
    public const string ReverseProxyDynamic = "$config/traefik";

    public static bool IsRelativeVolume(string volumeKey) => volumeKey.StartsWith('$');
    public static string Combine(string baseVolume, string extension) => $"{baseVolume}/{extension}";
}

public class VolumePathResolver
{
    readonly StorageConfig storage;
    readonly string dataRoot;
    readonly HostPathMapper hostPathMapper;

    public VolumePathResolver(StorageConfig storage, string? dataRoot = null, HostPathMapper? hostPathMapper = null)
    {
        this.storage = storage;
        this.dataRoot = dataRoot ?? DataRoute.Root;
        this.hostPathMapper = hostPathMapper ?? HostPathMapper.Default;
    }

    // Flattens the relative volume path to only include root relative volumes, which can then
    // be used to resolve into host or container paths.
    public string FlattenRelativeVolumes(string volumeKey) => volumeKey switch
    {
        RelativeVolumes.Tv => storage.Tv,
        RelativeVolumes.TvMovies => $"{storage.Tv}/movies",
        RelativeVolumes.TvShows => $"{storage.Tv}/shows",
        RelativeVolumes.Music => storage.Music,
        RelativeVolumes.DockerSocket => DockerSocketPath,
        _ when volumeKey.StartsWith(RelativeVolumes.Config, StringComparison.Ordinal) =>
            $"{RelativeVolumes.Config}/{ConfigSubPath(volumeKey)}",
        _ => volumeKey
    };

    // Accepts both the colon form ("$config:radarr") and the natural slash form
    // ("$config/radarr"). Returns the sub-path without the "$config" root so it is never
    // doubled when the root is substituted in later.
    static string ConfigSubPath(string volumeKey)
    {
        string suffix = volumeKey[RelativeVolumes.Config.Length..];
        return suffix.TrimStart(':', '/');
    }

    public string ResolveToHostPath(string volumeKey)
    {
        // Docker needs host paths for bind sources. Absolute storage roots are already host
        // paths and are used verbatim. Relative roots are anchored to the data route (a path
        // inside Stackarr's container) and translated to the host via the discovered mounts.
        return FlattenRelativeVolumes(volumeKey)
            .Replace(RelativeVolumes.Downloads, HostRoot(storage.Downloads))
            .Replace(RelativeVolumes.Media, HostRoot(storage.Media))
            .Replace(RelativeVolumes.Config, HostRoot(storage.Config));
    }

    // The path as seen by this process (data route applied, but before translating to the
    // host). In a container this is a mount-backed path (e.g. /media/tv); standalone it is
    // the on-disk path (e.g. <cwd>/media/tv). Used when creating directories this process
    // can reach directly.
    public string ResolveToLocalPath(string volumeKey) =>
        FlattenRelativeVolumes(volumeKey)
            .Replace(RelativeVolumes.Downloads, Anchor(storage.Downloads))
            .Replace(RelativeVolumes.Media, Anchor(storage.Media))
            .Replace(RelativeVolumes.Config, Anchor(storage.Config));

    public string ResolveToContainerPath(string volumeKey) =>
        FlattenRelativeVolumes(volumeKey)
            .Replace(RelativeVolumes.Downloads, "/downloads")
            .Replace(RelativeVolumes.Media, "/media")
            .Replace(RelativeVolumes.Config, "/config");

    // Resolves a storage root to the host path Docker needs. An absolute configured root is
    // a host path already; a relative root is anchored to the data route and then translated
    // through Stackarr's own container mounts when running in Docker.
    string HostRoot(string path) =>
        Path.IsPathRooted(path) ? path : hostPathMapper.ToHostPath(Anchor(path));

    // Anchors a configured storage root to the data route when it is relative. Absolute
    // roots (e.g. "/mnt/stackarr/media") are returned unchanged.
    string Anchor(string path) => DataRoute.ResolveUnder(dataRoot, path);

    static string DockerSocketPath =>
        Environment.GetEnvironmentVariable("DOCKER_SOCKET") ?? "/var/run/docker.sock";
}
