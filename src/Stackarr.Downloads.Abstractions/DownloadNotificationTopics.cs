namespace Stackarr.Downloads.Abstractions;

/// <summary>Hierarchical notification topics for the torrent/download domain.</summary>
public static class DownloadNotificationTopics
{
    // downloads.torrent
    public const string TorrentAdded = "downloads.torrent.added";
    public const string TorrentRemoved = "downloads.torrent.removed";
    public const string TorrentError = "downloads.torrent.error";
    public const string TorrentStriked = "downloads.torrent.struck";
    public const string TorrentSlow = "downloads.torrent.slow";
    public const string DownloadStalled = "downloads.torrent.stalled";
    public const string SeedingCompleted = "downloads.torrent.seeding.completed";

    // downloads
    public const string DownloadProgress = "downloads.progress";
    public const string DownloadPaused = "downloads.paused";
    public const string DownloadResumed = "downloads.resumed";
    public const string DownloadCompleted = "downloads.completed";
    public const string DownloadImported = "downloads.imported";
    public const string DownloadReady = "downloads.ready";
    public const string DownloadFailed = "downloads.failed";
    public const string DownloadPausedStorage = "downloads.paused.storage";

    // downloads.soulseek
    public const string SoulseekGrabbed = "downloads.soulseek.grabbed";

    // downloads.music
    public const string MusicAlbumDownloadFailed = "downloads.music.album.failed";
}

