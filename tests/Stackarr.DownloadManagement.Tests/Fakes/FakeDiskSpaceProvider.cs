namespace Stackarr.DownloadManagement.Tests;

/// <summary>
/// Disk-space probe that can be configured with fixed values or a per-call sequence.
/// </summary>
public sealed class FakeDiskSpaceProvider : IDiskSpaceProvider
{
    public bool Result { get; set; } = true;
    public long Total { get; set; } = 1_000_000;
    public long Used { get; set; }

    /// <summary>Per-call overrides consumed before falling back to Total/Used.</summary>
    public Queue<(bool Result, long Total, long Used)> Sequence { get; } = new();

    public int Calls { get; private set; }
    public List<string> Paths { get; } = [];

    public bool TryGetDiskSpace(string path, out long total, out long free, out long used)
    {
        Calls++;
        Paths.Add(path);

        bool result;
        if (Sequence.Count > 0)
        {
            (result, total, used) = Sequence.Dequeue();
        }
        else
        {
            result = Result;
            total = Total;
            used = Used;
        }

        free = Math.Max(0, total - used);
        return result;
    }

    public bool TryGetFileSystemId(string path, out ulong fileSystemId)
    {
        fileSystemId = 0;
        return false;
    }
}
