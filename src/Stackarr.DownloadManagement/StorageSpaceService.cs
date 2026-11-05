using Microsoft.Extensions.Options;
using Stackarr.Core.Configuration;
using Stackarr.Core.Services;

namespace Stackarr.DownloadManagement;

/// <summary>Disk usage for one storage root, as shown on the dashboard.</summary>
public sealed record StorageSpace(string Label, string Path, long TotalBytes, long FreeBytes, long UsedBytes)
{
    public double UsedPercent => TotalBytes > 0 ? (double)UsedBytes / TotalBytes * 100 : 0;
}

/// <summary>
/// Probes the media and downloads roots so the dashboard can show free space. Paths are resolved
/// through <see cref="VolumePathResolver"/> on every call, so storage config changes apply live.
/// The same probe backs the storage thresholds in <c>StorageCheckJob</c>.
/// </summary>
public sealed class StorageSpaceService(
    IOptionsMonitor<StackarrConfig> configMonitor,
    IDiskSpaceProvider diskSpace)
{
    static readonly (string Label, string VolumeKey)[] Roots =
    [
        ("Media", RelativeVolumes.Media),
        ("Downloads", RelativeVolumes.Downloads)
    ];

    public IReadOnlyList<StorageSpace> GetSpaces()
    {
        VolumePathResolver resolver = new(configMonitor.CurrentValue.Storage);
        List<StorageSpace> spaces = [];
        HashSet<string> seenPaths = new(StringComparer.Ordinal);
        HashSet<ulong> seenFileSystems = [];

        foreach ((string label, string volumeKey) in Roots)
        {
            string path = resolver.ResolveToLocalPath(volumeKey);
            if (!seenPaths.Add(Path.GetFullPath(path)))
                continue;

            if (!diskSpace.TryGetDiskSpace(path, out long total, out long free, out long used) || total <= 0)
                continue;

            // Media and downloads usually live on the same disk; show that disk once rather than
            // repeating identical bars. An unknown id (0) must not collapse distinct roots.
            if (diskSpace.TryGetFileSystemId(path, out ulong fileSystemId) && fileSystemId != 0
                && !seenFileSystems.Add(fileSystemId))
            {
                continue;
            }

            spaces.Add(new(label, path, total, free, used));
        }

        return spaces;
    }
}
