using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stackarr.Core;
using Stackarr.Core.Events;
using Stackarr.Core.Models;
using Stackarr.Core.Notifications;
using Stackarr.Core.Services;

namespace Stackarr.DownloadManagement;

/// <summary>
/// Checks every downloading torrent once a minute and accrues penalty minutes for slow or stalled
/// torrents. A torrent still under 5% (fake seeders) accrues faster. When the penalty reaches the
/// configured limit the torrent is removed. Healthy torrents lose penalty minutes again, and while
/// our own connection is unhealthy nothing is accrued or removed.
/// </summary>
public class TorrentHealthDetector(
    ITorrentDownloader torrentService,
    IOptionsMonitor<TorrentDownloaderConfig> config,
    IConnectionHealthProvider connectionHealth,
    IEventBus eventBus,
    IEventStore eventStore,
    TorrentPenaltyTracker penalties,
    TimeProvider time,
    ILogger<TorrentHealthDetector> logger)
{
    public async Task CheckTorrents(CancellationToken ct = default)
    {
        DownloadManagementConfig management = config.CurrentValue.Management;
        bool slowEnabled = management.Slow.Enabled;
        bool stalledEnabled = management.Stalled.Enabled;
        if (!slowEnabled && !stalledEnabled)
            return;

        List<TorrentItem> torrents = (await torrentService.GetTorrents("all", ct))
            .Where(TorrentHealth.IsIncompleteCandidate)
            .ToList();

        DateTime now = time.GetUtcNow().UtcDateTime;
        ConnectionHealth health = await connectionHealth.GetDownloadConnectionHealthAsync(ct);

        if (!health.IsHealthy)
        {
            // Our own connection is the problem. Hold every clock so the downtime is not later
            // counted against a torrent, and clean nothing up.
            logger.LogInformation(
                "Skipping torrent slow/stalled cleanup: {Target} connection unhealthy ({Download:F1} Mbps)",
                health.Target, health.DownloadMbps ?? 0);
            foreach (TorrentItem torrent in torrents)
                penalties.Hold(torrent.Hash, now);

            penalties.Prune(torrents.Select(t => t.Hash));
            return;
        }

        double slowThresholdBytes = management.Slow.ThresholdMbps * 1024 * 1024;

        foreach (TorrentItem torrent in torrents)
        {
            TorrentHealthReason? reason = TorrentHealth.Classify(torrent, slowThresholdBytes, slowEnabled, stalledEnabled);
            if (reason is null)
            {
                penalties.Decay(torrent.Hash, now, management.RecoveryPerMinute);
                continue;
            }

            bool zeroProgress = TorrentHealth.IsZeroProgress(torrent);
            double weight = zeroProgress ? Math.Max(1, management.ZeroProgressMultiplier) : 1;
            double limit = Math.Max(1, reason == TorrentHealthReason.Stalled
                ? management.Stalled.LimitMinutes
                : management.Slow.LimitMinutes);

            PenaltyResult penalty = penalties.Accrue(
                torrent, reason.Value, zeroProgress, now, weight, limit);

            await NotifyFirstSightingAsync(torrent, penalty, now, ct);

            if (penalty.PenaltyMinutes >= penalty.LimitMinutes && penalties.TryClaimDeletion(torrent.Hash))
                await DeleteAsync(torrent, penalty, now, ct);
        }

        // Drop state for torrents that are no longer candidates.
        penalties.Prune(torrents.Select(t => t.Hash));
    }

    async Task NotifyFirstSightingAsync(TorrentItem torrent, PenaltyResult penalty, DateTime now, CancellationToken ct)
    {
        if (!penalty.FirstSighting)
            return;

        if (penalty.Reason == TorrentHealthReason.Stalled)
        {
            // The download event monitor owns the user-visible "torrent stalled" notification.
            logger.LogInformation("Torrent {Name} ({Hash}) is stalled (no peers)", torrent.Name, torrent.Hash);
            return;
        }

        SlowTorrentConfig slow = config.CurrentValue.Management.Slow;
        await eventStore.RecordAsync(new SystemEventRecord
        {
            Category = EventCategory.Download,
            EventType = DownloadEventType.Slow,
            Severity = EventSeverity.Warning,
            Message = $"Torrent is downloading slowly (below {slow.ThresholdMbps} MB/s): {torrent.Name}",
            Source = torrent.Hash,
            SourceName = torrent.Name,
            Notification = new NotificationMessage
            {
                Key = DownloadNotificationTopics.TorrentSlow,
                Title = "Slow download warning",
                Body = $"Downloading slowly (below {slow.ThresholdMbps} MB/s): {torrent.Name}",
                Severity = EventSeverity.Warning,
                Source = torrent.Hash,
                SourceName = torrent.Name,
                Timestamp = now,
                Fields =
                [
                    new("Speed", $"{torrent.DownloadSpeed / (1024 * 1024):F2} MB/s"),
                    new("Threshold", $"{slow.ThresholdMbps} MB/s"),
                    new("Limit", $"{slow.LimitMinutes} min")
                ]
            },
            Timestamp = now
        }, ct);
    }

    async Task DeleteAsync(TorrentItem torrent, PenaltyResult penalty, DateTime now, CancellationToken ct)
    {
        bool stalled = penalty.Reason == TorrentHealthReason.Stalled;
        string reasonText = (stalled, penalty.ZeroProgress) switch
        {
            (true, true) => "stalled under 5% (no seeders)",
            (true, false) => "stalled with no peers",
            (false, true) => "below the speed threshold under 5% (no seeders)",
            (false, false) => "below the speed threshold"
        };

        logger.LogWarning(
            "Torrent {Name} ({Hash}) reached its {Limit:F0} min limit after {Penalty:F1} penalty min {Reason}, deleting",
            torrent.Name, torrent.Hash, penalty.LimitMinutes, penalty.PenaltyMinutes, reasonText);

        await torrentService.DeleteTorrent(torrent.Hash, TorrentFileCleanup.DeleteFiles, ct);

        await eventStore.RecordAsync(new SystemEventRecord
        {
            Category = EventCategory.Download,
            EventType = DownloadEventType.Struck,
            Severity = EventSeverity.Warning,
            Message = $"Torrent removed after {penalty.PenaltyMinutes:F0} penalty min {reasonText}: {torrent.Name}",
            Source = torrent.Hash,
            SourceName = torrent.Name,
            Notification = new NotificationMessage
            {
                Key = DownloadNotificationTopics.TorrentStriked,
                Title = stalled ? "Stalled torrent removed" : "Slow torrent removed",
                Body = $"Removed after {penalty.PenaltyMinutes:F0} min {reasonText}: {torrent.Name}",
                Severity = EventSeverity.Warning,
                Source = torrent.Hash,
                SourceName = torrent.Name,
                Timestamp = now,
                Fields =
                [
                    new("Penalty", $"{penalty.PenaltyMinutes:F0} min"),
                    new("Limit", $"{penalty.LimitMinutes:F0} min"),
                    new("Progress", $"{(int)Math.Floor(torrent.Progress * 100)}%"),
                    new("Speed", $"{torrent.DownloadSpeed / (1024 * 1024):F2} MB/s")
                ]
            },
            Timestamp = now
        }, ct);

        await eventBus.Publish(new TorrentDeletedEvent(
            torrent.Hash,
            torrent.Name,
            stalled ? TorrentDeleteReason.Stalled : TorrentDeleteReason.Slow,
            now,
            torrent.Category,
            torrent.Labels), ct);
    }
}
