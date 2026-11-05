namespace Stackarr.Downloads.Abstractions;

public class TorrentItem
{
    public string Hash { get; init; } = "";
    public string Name { get; init; } = "";
    public string Category { get; init; } = "";

    /// <summary>Every client label on the torrent (e.g. the arr category plus Stackarr's own markers).</summary>
    public IReadOnlyList<string> Labels { get; init; } = [];
    public double Progress { get; init; }
    public double DownloadSpeed { get; init; }
    public double UploadSpeed { get; init; }
    public double Ratio { get; init; }
    /// <summary>Total seconds this torrent has spent seeding (Transmission's <c>secondsSeeding</c>).</summary>
    public long SecondsSeeding { get; init; }
    public int Seeds { get; init; }
    public int Peers { get; init; }
    public TorrentState State { get; init; }
    public DateTime AddedOn { get; init; }
    public long Size { get; init; }
    public long Downloaded { get; init; }
    public string RemainingTime { get; init; } = "";
}

public enum TorrentState
{
    Downloading,
    Seeding,
    Paused,
    Queued,
    Error,
    MissingFiles,
    Unknown
}

public class TorrentTransferStats
{
    public long DownloadSpeed { get; init; }
    public long UploadSpeed { get; init; }
    public long TotalDownloaded { get; init; }
    public long TotalUploaded { get; init; }
}
