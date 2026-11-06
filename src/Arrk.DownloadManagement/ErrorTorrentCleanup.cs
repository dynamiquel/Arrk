using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Arrk.Core;
using Arrk.Core.Events;
using Arrk.Core.Models;
using Arrk.Core.Notifications;
using Arrk.Core.Services;

namespace Arrk.DownloadManagement;

public class ErrorTorrentCleanup(
    ITorrentDownloader torrentService,
    IOptionsMonitor<TorrentDownloaderConfig> config,
    IEventBus eventBus,
    IEventStore eventStore,
    TimeProvider time,
    ILogger<ErrorTorrentCleanup> logger)
{
    readonly ConcurrentDictionary<string, DateTime> _errorTimestamps = new();

    public async Task CleanupErroredTorrents(CancellationToken ct = default)
    {
        ErrorTorrentConfig errors = config.CurrentValue.Management.Errors;
        if (!errors.Enabled)
            return;

        List<TorrentItem> torrents = await torrentService.GetTorrents("error", ct);

        HashSet<string> currentHashes = new HashSet<string>(torrents.Select(t => t.Hash));
        foreach (string key in _errorTimestamps.Keys.ToList())
        {
            if (!currentHashes.Contains(key))
                _errorTimestamps.TryRemove(key, out _);
        }

        foreach (TorrentItem torrent in torrents)
        {
            if (!_errorTimestamps.ContainsKey(torrent.Hash))
            {
                _errorTimestamps[torrent.Hash] = time.GetUtcNow().UtcDateTime;
                continue;
            }

            DateTime errorSince = _errorTimestamps[torrent.Hash];
            double elapsed = (time.GetUtcNow().UtcDateTime - errorSince).TotalMinutes;

            // Atomically claim the deletion so overlapping runs delete at most once.
            if (elapsed >= errors.DeleteAfterMinutes && _errorTimestamps.TryRemove(torrent.Hash, out _))
            {
                logger.LogWarning("Deleting errored torrent {Name} ({Hash}) after {Minutes:F1} minutes",
                    torrent.Name, torrent.Hash, elapsed);

                await eventStore.RecordAsync(new SystemEventRecord
                {
                    Category = EventCategory.Download,
                    EventType = DownloadEventType.Error,
                    Severity = EventSeverity.Error,
                    Message = $"Errored torrent deleted after {elapsed:F0} min: {torrent.Name}",
                    Source = torrent.Hash,
                    SourceName = torrent.Name,
                    Notification = new NotificationMessage
                    {
                        Key = DownloadNotificationTopics.TorrentError,
                        Title = "Torrent error",
                        Body = $"Errored torrent deleted after {elapsed:F0} min: {torrent.Name}",
                        Severity = EventSeverity.Error,
                        Source = torrent.Hash,
                        SourceName = torrent.Name,
                        Timestamp = time.GetUtcNow().UtcDateTime,
                        Fields = [new("Errored for", $"{elapsed:F0} min")]
                    },
                    Timestamp = time.GetUtcNow().UtcDateTime
                }, ct);

                await torrentService.DeleteTorrent(torrent.Hash, TorrentFileCleanup.DeleteFiles, ct);

                await eventBus.Publish(new TorrentDeletedEvent(
                    torrent.Hash,
                    torrent.Name,
                    TorrentDeleteReason.Error,
                    time.GetUtcNow().UtcDateTime,
                    torrent.Category,
                    torrent.Labels), ct);
            }
        }
    }
}
