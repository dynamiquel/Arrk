using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Arrk.Core;
using Arrk.Core.Events;
using Arrk.Core.Models;
using Arrk.Core.Notifications;
using Arrk.Core.Services;

namespace Arrk.DownloadManagement;

/// <summary>
/// Enforces the seeding limits Arrk owns: once a finished torrent has uploaded to the
/// configured ratio, or has been seeding for the configured time, it is removed (files included)
/// and a <c>downloads.torrent.seeding.completed</c> event is emitted.
/// </summary>
/// <remarks>
/// Transmission only ever <em>pauses</em> a torrent at a seed limit; nothing deletes it. Arrk
/// therefore leaves Transmission's own ratio/idle limits alone and removes completed torrents here,
/// on a periodic check, so that seeding goals and cleanup live in one place.
/// </remarks>
public class SeedingLimitCleanup(
    ITorrentDownloader torrentService,
    IOptionsMonitor<TorrentDownloaderConfig> config,
    IEventBus eventBus,
    IEventStore eventStore,
    TimeProvider time,
    ILogger<SeedingLimitCleanup> logger)
{
    public async Task CleanupCompletedSeeding(CancellationToken ct = default)
    {
        SeedingConfig seeding = config.CurrentValue.Seeding;

        // Both goals disabled: there is nothing to enforce.
        if (seeding.MaxRatio <= 0 && seeding.MaxTimeMinutes <= 0)
            return;

        List<TorrentItem> torrents = await torrentService.GetTorrents("all", ct);
        DateTime now = time.GetUtcNow().UtcDateTime;
        long minSeedSeconds = Math.Max(0, seeding.MinTimeMinutes) * 60L;

        foreach (TorrentItem torrent in torrents)
        {
            // Only finished torrents are seeding; a partial one is still downloading.
            if (torrent.Progress < 1.0)
                continue;

            // Always honour the minimum seeding time, even when a ratio was hit immediately.
            if (torrent.SecondsSeeding < minSeedSeconds)
                continue;

            bool ratioReached = seeding.MaxRatio > 0 && torrent.Ratio >= seeding.MaxRatio;
            bool timeReached = seeding.MaxTimeMinutes > 0 && torrent.SecondsSeeding >= seeding.MaxTimeMinutes * 60L;
            if (!ratioReached && !timeReached)
                continue;

            logger.LogInformation(
                "Removing torrent {Name} ({Hash}) after seeding {Minutes:F0} min at ratio {Ratio:F2} ({Why})",
                torrent.Name,
                torrent.Hash,
                torrent.SecondsSeeding / 60.0,
                torrent.Ratio,
                ratioReached ? "ratio reached" : "time reached");

            await torrentService.DeleteTorrent(torrent.Hash, TorrentFileCleanup.DeleteFiles, ct);

            await eventStore.RecordAsync(new SystemEventRecord
            {
                Category = EventCategory.Download,
                EventType = DownloadEventType.SeedingCompleted,
                Severity = EventSeverity.Info,
                Message = $"Seeding complete, torrent removed: {torrent.Name}",
                Source = torrent.Hash,
                SourceName = torrent.Name,
                Notification = new NotificationMessage
                {
                    Key = DownloadNotificationTopics.SeedingCompleted,
                    Title = "Seeding complete",
                    Body = $"Removed torrent {torrent.Name} after it reached its seeding goal",
                    Severity = EventSeverity.Info,
                    Source = torrent.Hash,
                    SourceName = torrent.Name,
                    Timestamp = now,
                    Fields =
                    [
                        new("Seeded", FormatDuration(torrent.SecondsSeeding)),
                        new("Ratio", torrent.Ratio.ToString("F2"))
                    ]
                },
                Timestamp = now
            }, ct);

            await eventBus.Publish(new TorrentDeletedEvent(
                torrent.Hash,
                torrent.Name,
                TorrentDeleteReason.SeedingCompleted,
                now,
                torrent.Category,
                torrent.Labels), ct);
        }
    }

    static string FormatDuration(long seconds)
    {
        TimeSpan span = TimeSpan.FromSeconds(seconds);
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}h {span.Minutes}m"
            : $"{span.Minutes}m";
    }
}
