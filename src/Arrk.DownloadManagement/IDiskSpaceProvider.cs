using Arrk.Docker;

namespace Arrk.DownloadManagement;

/// <summary>
/// Abstraction over disk-space probing so storage cleanup logic can be tested
/// without touching the real filesystem.
/// </summary>
public interface IDiskSpaceProvider
{
    bool TryGetDiskSpace(string path, out long total, out long free, out long used);

    /// <summary>
    /// Identifies the filesystem containing <paramref name="path"/>, or returns false when the
    /// platform or path cannot report one.
    /// </summary>
    bool TryGetFileSystemId(string path, out ulong fileSystemId);
}

public sealed class DockerDiskSpaceProvider : IDiskSpaceProvider
{
    public bool TryGetDiskSpace(string path, out long total, out long free, out long used)
        => DockerMetricsCollector.GetDiskSpace(path, out total, out free, out used);

    public bool TryGetFileSystemId(string path, out ulong fileSystemId)
        => DockerMetricsCollector.GetFileSystemId(path, out fileSystemId);
}
