using Hangfire;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Arrk.Core;
using Arrk.Core.Events;
using Arrk.Core.Models;
using Arrk.Notifications;
using Arrk.Core.Services;

namespace Arrk.DownloadManagement.Jobs;

public class StorageCheckJob(
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<TorrentDownloaderConfig> configMonitor,
    IDiskSpaceProvider diskSpace,
    TimeProvider time,
    IEventStore eventStore,
    ILogger<StorageCheckJob> logger)
{
    public const string DefaultCron = "* * * * *";
    const string DefaultDownloadsPath = "/downloads";
    const double PercentDivisor = 100.0;

    [JobDisplayName("Storage Check")]
    public async Task Execute(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var torrentService = scope.ServiceProvider.GetRequiredService<ITorrentDownloader>();
        StorageCleanupConfig storage = configMonitor.CurrentValue.Management.Storage;

        if (!storage.Enabled)
            return;

        string downloadsPath = GetDownloadsPath();
        if (!diskSpace.TryGetDiskSpace(downloadsPath, out long total, out _, out long used))
        {
            logger.LogWarning("Could not read disk space for {Path}", downloadsPath);
            return;
        }

        if (total <= 0)
            return;

        double usedPercent = (double)used / total * PercentDivisor;

        if (usedPercent >= storage.PauseDownloadsAtPercent)
        {
            logger.LogWarning("Storage at {Percent:F1}% — pausing all active downloads", usedPercent);
            await torrentService.PauseTorrents(ct: ct);

            string message = $"Storage at {usedPercent:F1}% (threshold {storage.PauseDownloadsAtPercent}%) — all downloads paused";
            await eventStore.RecordAsync(
                Event("storage.critical", EventSeverity.Error, message,
                    StorageNotificationTopics.StorageCritical, "Storage critical",
                    [new("Used", $"{usedPercent:F1}%"), new("Threshold", $"{storage.PauseDownloadsAtPercent}%")]),
                ct);
            await eventStore.RecordAsync(
                Event("storage.downloads_paused", EventSeverity.Warning, message,
                    DownloadNotificationTopics.DownloadPausedStorage, "Downloads paused (storage full)",
                    [new("Used", $"{usedPercent:F1}%")]),
                ct);
        }
        else if (usedPercent >= storage.CleanupSeedingAtPercent)
        {
            logger.LogWarning("Storage at {Percent:F1}% — cleaning oldest seeding torrents", usedPercent);
            List<TorrentItem> deleted = await CleanupSeedingTorrents(torrentService, storage, total, downloadsPath, ct);

            if (deleted.Count == 0)
                return;

            string names = string.Join(", ", deleted.Take(5).Select(t => t.Name));
            string message = $"Storage at {usedPercent:F1}% — removed {deleted.Count} seeding torrent(s): {names}";

            await eventStore.RecordAsync(
                Event("storage.warning", EventSeverity.Warning, message,
                    StorageNotificationTopics.StorageLow, "Storage warning",
                    [new("Used", $"{usedPercent:F1}%"), new("Threshold", $"{storage.CleanupSeedingAtPercent}%")]),
                ct);
            await eventStore.RecordAsync(
                Event("storage.cleanup", EventSeverity.Info, message,
                    StorageNotificationTopics.StorageCleanup, "Storage cleanup",
                    [new("Removed", deleted.Count.ToString()), new("Strategy", storage.CleanupStrategy)]),
                ct);
        }
    }

    SystemEventRecord Event(
        string eventType,
        EventSeverity severity,
        string message,
        string notificationKey,
        string title,
        IReadOnlyList<NotificationField> fields) => new()
        {
            Category = EventCategory.Storage,
            EventType = eventType,
            Severity = severity,
            Message = message,
            Notification = new NotificationMessage
            {
                Key = notificationKey,
                Title = title,
                Body = message,
                Severity = severity,
                Timestamp = time.GetUtcNow().UtcDateTime,
                Fields = fields
            },
            Timestamp = time.GetUtcNow().UtcDateTime
        };

    async Task<List<TorrentItem>> CleanupSeedingTorrents(
        ITorrentDownloader torrentService,
        StorageCleanupConfig storage,
        long total,
        string downloadsPath,
        CancellationToken ct)
    {
        List<TorrentItem> deleted = [];
        List<TorrentItem> torrents = await torrentService.GetTorrents("seeding", ct);
        if (torrents.Count == 0)
            return deleted;

        TimeSpan minSeedTime = TimeSpan.FromMinutes(storage.MinSeedTimeMinutes);
        List<TorrentItem> sorted = storage.CleanupStrategy switch
        {
            "newest" => torrents.OrderByDescending(t => t.AddedOn).ToList(),
            "least_ratio" => torrents.OrderBy(t => t.Ratio).ToList(),
            _ => torrents.OrderBy(t => t.AddedOn).ToList()
        };

        foreach (TorrentItem torrent in sorted)
        {
            if (time.GetUtcNow().UtcDateTime - torrent.AddedOn < minSeedTime)
                continue;

            logger.LogInformation("Cleaning seeding torrent {Name} (ratio: {Ratio:F2})",
                torrent.Name, torrent.Ratio);
            await torrentService.DeleteTorrent(torrent.Hash, ct: ct);
            deleted.Add(torrent);

            if (!diskSpace.TryGetDiskSpace(downloadsPath, out _, out _, out long usedNow))
                break;

            // Stop once usage has dropped below the cleanup threshold that triggered this.
            if (total > 0 && (double)usedNow / total * PercentDivisor < storage.CleanupSeedingAtPercent)
                break;
        }

        return deleted;
    }

    static string GetDownloadsPath() =>
        Environment.GetEnvironmentVariable("DOWNLOADS_PATH") ?? DefaultDownloadsPath;
}
