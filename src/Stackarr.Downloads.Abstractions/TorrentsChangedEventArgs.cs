
namespace Stackarr.Downloads.Abstractions;

public class TorrentsChangedEventArgs : EventArgs
{
    public IReadOnlyList<TorrentItem> Torrents { get; init; } = [];
}

public class TorrentStateChangedEventArgs : EventArgs
{
    public TorrentItem Torrent { get; init; } = new();
}

public class DownloadManagementEventArgs : EventArgs
{
    public string EventType { get; init; } = "";
    public string Message { get; init; } = "";
    public string? TorrentHash { get; init; }
    public string? TorrentName { get; init; }
}
