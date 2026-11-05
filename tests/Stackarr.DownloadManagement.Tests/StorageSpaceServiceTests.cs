using Stackarr.Core.Configuration;
using Stackarr.TestSupport;

namespace Stackarr.DownloadManagement.Tests;

/// <summary>
/// Covers the dashboard storage probe: media and downloads are resolved from the live storage config,
/// each root is probed independently, and unusable roots are omitted rather than reported as zero.
/// </summary>
public class StorageSpaceServiceTests
{
    static StorageSpaceService Create(
        PathDiskSpaceProvider disk,
        StorageConfig? storage = null,
        MutableOptionsMonitor<StackarrConfig>? config = null)
    {
        config ??= new MutableOptionsMonitor<StackarrConfig>(new StackarrConfig
        {
            Storage = storage ?? new StorageConfig { Media = "/media", Downloads = "/downloads" }
        });
        return new StorageSpaceService(config, disk);
    }

    [Test]
    public async Task GetSpaces_ProbesMediaAndDownloads()
    {
        PathDiskSpaceProvider disk = new()
        {
            Entries =
            {
                ["/media"] = (true, 4_000L, 1_000L),
                ["/downloads"] = (true, 2_000L, 1_500L)
            }
        };

        IReadOnlyList<StorageSpace> spaces = Create(disk).GetSpaces();

        await Assert.That(spaces.Select(s => s.Label).ToArray()).IsEquivalentTo(["Media", "Downloads"]);
        await Assert.That(spaces[0].Path).IsEqualTo("/media");
        await Assert.That(spaces[0].TotalBytes).IsEqualTo(4_000L);
        await Assert.That(spaces[0].FreeBytes).IsEqualTo(3_000L);
        await Assert.That(spaces[0].UsedBytes).IsEqualTo(1_000L);
        await Assert.That(spaces[0].UsedPercent).IsEqualTo(25.0);
        await Assert.That(spaces[1].Path).IsEqualTo("/downloads");
        await Assert.That(spaces[1].FreeBytes).IsEqualTo(500L);
        await Assert.That(spaces[1].UsedPercent).IsEqualTo(75.0);
        await Assert.That(disk.Paths).IsEquivalentTo(["/media", "/downloads"]);
    }

    [Test]
    public async Task GetSpaces_SkipsRootsThatCannotBeProbedOrAreEmpty()
    {
        PathDiskSpaceProvider disk = new()
        {
            Entries =
            {
                ["/media"] = (false, 0L, 0L),
                ["/downloads"] = (true, 0L, 0L)
            }
        };

        IReadOnlyList<StorageSpace> spaces = Create(disk).GetSpaces();

        await Assert.That(spaces).IsEmpty();
    }

    [Test]
    public async Task GetSpaces_ShowsOneRootPerFileSystem()
    {
        PathDiskSpaceProvider disk = new()
        {
            Entries =
            {
                ["/media"] = (true, 4_000L, 1_000L),
                ["/downloads"] = (true, 4_000L, 1_000L)
            },
            FileSystemIds =
            {
                ["/media"] = 42,
                ["/downloads"] = 42
            }
        };

        IReadOnlyList<StorageSpace> spaces = Create(disk).GetSpaces();

        await Assert.That(spaces).HasSingleItem();
        await Assert.That(spaces[0].Label).IsEqualTo("Media");
        await Assert.That(spaces[0].Path).IsEqualTo("/media");
    }

    [Test]
    public async Task GetSpaces_DoesNotRepeatTheSameRoot()
    {
        PathDiskSpaceProvider disk = new()
        {
            Entries = { ["/data"] = (true, 4_000L, 1_000L) }
        };

        StorageSpaceService service = Create(
            disk,
            new StorageConfig { Media = "/data", Downloads = "/data" });

        IReadOnlyList<StorageSpace> spaces = service.GetSpaces();

        await Assert.That(spaces).HasSingleItem();
        await Assert.That(spaces[0].Label).IsEqualTo("Media");
        await Assert.That(disk.Paths).HasSingleItem();
    }

    [Test]
    public async Task GetSpaces_ResolvesPathsFromTheCurrentConfig()
    {
        PathDiskSpaceProvider disk = new()
        {
            Entries =
            {
                ["/mnt/one/media"] = (true, 1_000L, 100L),
                ["/mnt/one/downloads"] = (true, 1_000L, 100L)
            }
        };
        MutableOptionsMonitor<StackarrConfig> config = new(new StackarrConfig
        {
            Storage = new StorageConfig { Media = "/mnt/one/media", Downloads = "/mnt/one/downloads" }
        });
        StorageSpaceService service = Create(disk, config: config);

        IReadOnlyList<StorageSpace> before = service.GetSpaces();

        config.CurrentValue = new StackarrConfig
        {
            Storage = new StorageConfig { Media = "/mnt/two/media", Downloads = "/mnt/two/downloads" }
        };
        disk.Entries["/mnt/two/media"] = (true, 2_000L, 200L);
        disk.Entries["/mnt/two/downloads"] = (true, 2_000L, 200L);

        IReadOnlyList<StorageSpace> after = service.GetSpaces();

        await Assert.That(before[0].Path).IsEqualTo("/mnt/one/media");
        await Assert.That(after[0].Path).IsEqualTo("/mnt/two/media");
    }

    sealed class PathDiskSpaceProvider : IDiskSpaceProvider
    {
        public Dictionary<string, (bool Result, long Total, long Used)> Entries { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, ulong> FileSystemIds { get; } = new(StringComparer.Ordinal);
        public List<string> Paths { get; } = [];

        public bool TryGetDiskSpace(string path, out long total, out long free, out long used)
        {
            Paths.Add(path);

            if (Entries.TryGetValue(path, out (bool Result, long Total, long Used) entry) && entry.Result)
            {
                total = entry.Total;
                used = entry.Used;
                free = Math.Max(0, total - used);
                return true;
            }

            total = 0;
            free = 0;
            used = 0;
            return false;
        }

        public bool TryGetFileSystemId(string path, out ulong fileSystemId)
        {
            if (FileSystemIds.TryGetValue(path, out fileSystemId))
                return true;

            fileSystemId = 0;
            return false;
        }
    }
}
