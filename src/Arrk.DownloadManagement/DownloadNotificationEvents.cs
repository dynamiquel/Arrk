using Arrk.Notifications;
using Arrk.Downloads.Abstractions;

namespace Arrk.DownloadManagement;

/// <summary>Contributes the core torrent/download notification catalogue.</summary>
public sealed class DownloadNotificationEvents : INotificationEventProvider
{
    public IReadOnlyList<NotificationEventDefinition> Events { get; } =
    [
        new(DownloadNotificationTopics.TorrentAdded, true, "A torrent was added", "📥", 0x3498DB),
        new(DownloadNotificationTopics.TorrentRemoved, true, "A torrent was removed", "🗑️", 0x95A5A6),
        new(DownloadNotificationTopics.TorrentError, true, "A torrent entered an error state", "❌", 0xE74C3C),
        new(DownloadNotificationTopics.TorrentStriked, true, "A slow or stalled torrent was removed for lack of progress", "🥊", 0xE67E22),
        new(DownloadNotificationTopics.TorrentSlow, true, "A torrent is downloading slowly", "🐌", 0xE67E22),
        new(DownloadNotificationTopics.DownloadStalled, true, "A torrent stalled with no peers", "⏸️", 0xE67E22),
        new(DownloadNotificationTopics.SeedingCompleted, true, "A torrent reached its seeding goal", "🌱", 0x2ECC71),

        new(DownloadNotificationTopics.DownloadProgress, false, "Periodic download progress (noisy)", "⏳", 0x3498DB),
        new(DownloadNotificationTopics.DownloadPaused, false, "A download was paused", "⏸️", 0x3498DB),
        new(DownloadNotificationTopics.DownloadResumed, false, "A download was resumed", "▶️", 0x3498DB),
        new(DownloadNotificationTopics.DownloadCompleted, true, "A download finished", "✅", 0x2ECC71),
        new(DownloadNotificationTopics.DownloadImported, true, "A download was imported into the library", "📚", 0x2ECC71),
        new(DownloadNotificationTopics.DownloadReady, true, "Imported media is ready to play", "🎉", 0x2ECC71),
        new(DownloadNotificationTopics.DownloadFailed, true, "A download failed", "❌", 0xE74C3C),
        new(DownloadNotificationTopics.DownloadPausedStorage, true, "Downloads were paused because storage is full", "💾", 0xE67E22),

        new(DownloadNotificationTopics.SoulseekGrabbed, true, "A song was queued from a Soulseek peer", "👥", 0x1ABC9C),
        new(DownloadNotificationTopics.MusicAlbumDownloadFailed, true, "A music download failed and is being re-sourced", "⚠️", 0xE74C3C),
    ];

    public IReadOnlyList<NotificationGroupDefinition> Groups { get; } =
    [
        new("downloads", "Downloads", "Download lifecycle events"),
        new("downloads.paused", "Paused downloads", "Downloads paused or resumed"),
        new("downloads.torrent", "Torrents", "Torrent-level lifecycle events"),
        new("downloads.torrent.seeding", "Seeding", "Seeding completion"),
        new("downloads.soulseek", "Soulseek", "Soulseek transfers"),
        new("downloads.music", "Music downloads", "Music download failures"),
    ];
}
