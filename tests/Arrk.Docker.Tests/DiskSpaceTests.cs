using Arrk.Docker;

namespace Arrk.Docker.Tests;

/// <summary>
/// Covers the native disk probe used by dashboard storage, volume metrics and the storage
/// thresholds. The glibc <c>struct statvfs</c> is 112 bytes; under-declaring it lets the native
/// call write past the managed buffer and corrupt the caller.
/// </summary>
public class DiskSpaceTests
{
    [Test]
    public async Task GetDiskSpace_ReadsARealDirectory()
    {
        if (!OperatingSystem.IsLinux())
            return;

        bool ok = DockerMetricsCollector.GetDiskSpace(Path.GetTempPath(), out long total, out long free, out long used);

        await Assert.That(ok).IsTrue();
        await Assert.That(total).IsGreaterThan(0);
        await Assert.That(free).IsGreaterThanOrEqualTo(0);
        await Assert.That(used).IsGreaterThanOrEqualTo(0);
    }

    [Test]
    public async Task GetFileSystemId_MatchesForPathsOnTheSameFileSystem()
    {
        if (!OperatingSystem.IsLinux())
            return;

        string child = Directory.CreateTempSubdirectory().FullName;
        try
        {
            bool parentOk = DockerMetricsCollector.GetFileSystemId(Path.GetTempPath(), out ulong parent);
            bool childOk = DockerMetricsCollector.GetFileSystemId(child, out ulong nested);

            await Assert.That(parentOk).IsTrue();
            await Assert.That(childOk).IsTrue();
            await Assert.That(parent).IsNotEqualTo(0UL);
            await Assert.That(nested).IsEqualTo(parent);
        }
        finally
        {
            Directory.Delete(child);
        }
    }
}
