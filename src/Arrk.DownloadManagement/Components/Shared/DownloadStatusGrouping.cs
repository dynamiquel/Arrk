using Arrk.Core.Models;
using Arrk.Ui.Components;

namespace Arrk.DownloadManagement.Components.Shared;

/// <summary>
/// Groups the unified downloads surface (torrents and Soulseek transfers) into the shared
/// collapsible status sections. The vocabulary and bucketing live in <see cref="StatusGrouping"/>;
/// this supplies the download-specific classifiers.
/// </summary>
public static class DownloadStatusGrouping
{
    /// <summary>
    /// Torrents: by transmission state. MissingFiles counts as an error, and the
    /// transient/unknown states (Queued, Unknown) fall into "Other".
    /// </summary>
    public static StatusGroupKind GroupOfTorrent(TorrentItem torrent) => torrent.State switch
    {
        TorrentState.Downloading => StatusGroupKind.Downloading,
        TorrentState.Seeding => StatusGroupKind.Seeding,
        TorrentState.Paused => StatusGroupKind.Paused,
        TorrentState.Queued => StatusGroupKind.Queued,
        TorrentState.Error or TorrentState.MissingFiles => StatusGroupKind.Error,
        _ => StatusGroupKind.Other
    };

    public static IReadOnlyList<StatusGroup<TorrentItem>> ForTorrents(IEnumerable<TorrentItem> torrents) =>
        StatusGrouping.Build(torrents, GroupOfTorrent, StatusGrouping.TorrentSections);

    /// <summary>Unified downloads: torrents and Soulseek transfers grouped by their shared state.</summary>
    public static IReadOnlyList<StatusGroup<DownloadItem>> ForDownloads(IEnumerable<DownloadItem> downloads) =>
        StatusGrouping.Build(downloads, d => d.StateKind, StatusGrouping.DownloadSections);
}
