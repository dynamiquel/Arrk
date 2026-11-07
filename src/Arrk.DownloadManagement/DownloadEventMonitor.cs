using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Arrk.Core.Events;
using Arrk.Core.Models;
using Arrk.Core.Notifications;
using Arrk.Core.Services;
using Arrk.Downloads.Abstractions;
using Arrk.Media.Abstractions;

namespace Arrk.DownloadManagement;

/// <summary>
/// Watches the torrent list and records user-visible download events into the unified
/// event log: torrent added, progress at 10% steps, state changes (paused/resumed/error),
/// completion, stalling, and removal.
/// </summary>
/// <remarks>
/// State is kept per torrent so events are emitted only on transitions, and progress events
/// only when crossing a 10% boundary. The baseline is hydrated from the event log on startup,
/// so a restart does not re-announce existing torrents and does not re-fire completed events.
/// </remarks>
public class DownloadEventMonitor(
    ITorrentDownloader torrentService,
    IEventStore eventStore,
    IEnumerable<IMediaDownloadEntityResolver> entityResolvers,
    TimeProvider time,
    ILogger<DownloadEventMonitor> logger)
{
    const double ProgressStep = 0.10;
    static readonly TimeSpan EntityCacheLifetime = TimeSpan.FromMinutes(10);
    static readonly TimeSpan NegativeEntityCacheLifetime = TimeSpan.FromMinutes(1);

    sealed class TorrentSnapshot
    {
        public string Name { get; set; } = "";
        public TorrentState State { get; set; }
        public int ProgressBucket { get; set; }
        public bool Completed { get; set; }
        public bool StalledReported { get; set; }
    }

    readonly ConcurrentDictionary<string, TorrentSnapshot> _snapshots = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, EntityCacheEntry> _entities = new(StringComparer.OrdinalIgnoreCase);
    readonly IReadOnlyList<IMediaDownloadEntityResolver> _entityResolvers = entityResolvers.ToList();
    readonly SemaphoreSlim _pollLock = new(1, 1);
    bool _hydrated;

    /// <summary>
    /// Polls the torrent list once and records any lifecycle events that occurred since the
    /// previous call.
    /// </summary>
    public async Task PollAsync(CancellationToken ct = default)
    {
        // Serialise polls so overlapping runs cannot interleave snapshot updates.
        await _pollLock.WaitAsync(ct);
        try
        {
            await HydrateAsync(ct);
            await PollCoreAsync(ct);
        }
        finally
        {
            _pollLock.Release();
        }
    }

    async Task PollCoreAsync(CancellationToken ct)
    {
        List<TorrentItem> torrents;
        try
        {
            torrents = await torrentService.GetTorrents("all", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Download event monitor could not read torrents this cycle");
            return;
        }

        DateTime now = time.GetUtcNow().UtcDateTime;
        List<SystemEventRecord> events = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (TorrentItem torrent in torrents)
        {
            seen.Add(torrent.Hash);

            if (_snapshots.TryGetValue(torrent.Hash, out TorrentSnapshot? previous))
            {
                CollectChanges(previous, torrent, now, events);
                _snapshots[torrent.Hash] = Snapshot(torrent, stalledReported: TorrentHealth.IsStalled(torrent));
            }
            else
            {
                events.Add(New(torrent, DownloadEventType.Added, EventSeverity.Info,
                    $"Torrent added: {torrent.Name}", now));

                _snapshots[torrent.Hash] = Snapshot(torrent, stalledReported: false);
            }
        }

        // Anything we tracked that is gone was removed from the client.
        foreach (string hash in _snapshots.Keys)
        {
            if (seen.Contains(hash))
                continue;

            if (_snapshots.TryRemove(hash, out TorrentSnapshot? removed))
            {
                events.Add(New(hash, removed.Name, DownloadEventType.Removed, EventSeverity.Warning,
                    $"Torrent removed: {removed.Name}", now, null,
                    NotificationFor(DownloadNotificationTopics.TorrentRemoved,
                        NotificationTitles.TitleFor(DownloadNotificationTopics.TorrentRemoved),
                        removed.Name, hash,
                        NotificationBodyFor(DownloadNotificationTopics.TorrentRemoved, removed.Name),
                        EventSeverity.Warning, now)));
            }
        }

        if (events.Count > 0)
        {
            await AttachEntitiesAsync(events, ct);
            await PersistAsync(events, ct);
        }
    }

    /// <summary>
    /// Resolves media identity for download events that also notify subscribers. A
    /// torrent that no library owns stays a normal broadcast, so admin visibility is preserved.
    /// </summary>
    async Task AttachEntitiesAsync(List<SystemEventRecord> events, CancellationToken ct)
    {
        for (int index = 0; index < events.Count; index++)
        {
            SystemEventRecord item = events[index];
            NotificationMessage? notification = item.Notification;
            if (notification is null || item.Source is null || !TargetsSubscribers(notification.Key))
                continue;

            MediaEntityRef? entity = await ResolveEntityAsync(item.Source, ct);
            if (entity is null)
                continue;

            events[index] = item with
            {
                Notification = Localize(notification, entity)
            };
        }
    }

    /// <summary>
    /// Replaces the torrent-centric wording with the media title for subscriber DMs. The audit
    /// event keeps the technical message; only the user-facing notification changes.
    /// </summary>
    static NotificationMessage Localize(NotificationMessage message, MediaEntityRef entity)
    {
        string display = string.IsNullOrWhiteSpace(entity.Subtitle)
            ? entity.Title
            : $"{entity.Title} · {entity.Subtitle}";

        (string title, string body) = message.Key switch
        {
            DownloadNotificationTopics.TorrentAdded
                => ("Download started", $"{display} is downloading."),
            DownloadNotificationTopics.DownloadCompleted
                => ("Download finished", $"{display} finished downloading and is waiting to import."),
            DownloadNotificationTopics.DownloadReady
                => ("Ready to play", $"{display} is ready to play."),
            DownloadNotificationTopics.DownloadFailed
                => ("Download failed", $"{display} failed to download."),
            DownloadNotificationTopics.TorrentError
                => ("Download error", $"{display} hit an error while downloading."),
            DownloadNotificationTopics.DownloadStalled
                => ("Download stalled", $"{display} has no peers right now."),
            DownloadNotificationTopics.TorrentStriked
                => ("Download removed", $"{display} was removed because it made no progress."),
            _ => (message.Title, message.Body)
        };

        return message with
        {
            Title = title,
            Body = body,
            SourceName = entity.Title,
            Url = entity.Url,
            Entity = entity,
            Audience = NotificationAudience.BroadcastAndSubscribers,
            Style = message.Style ?? new NotificationStyle
            {
                ThumbnailUrl = entity.PosterUrl,
                ImageUrl = entity.BackdropUrl
            }
        };
    }

    async Task<MediaEntityRef?> ResolveEntityAsync(string hash, CancellationToken ct)
    {
        DateTime now = time.GetUtcNow().UtcDateTime;
        if (_entities.TryGetValue(hash, out EntityCacheEntry? cached) && cached.ExpiresAt > now)
            return cached.Entity;

        if (_entities.Count > 2000)
        {
            foreach ((string key, EntityCacheEntry entry) in _entities)
            {
                if (entry.ExpiresAt <= now)
                    _entities.TryRemove(key, out _);
            }
        }

        MediaEntityRef? entity = null;
        foreach (IMediaDownloadEntityResolver resolver in _entityResolvers)
        {
            try
            {
                entity = await resolver.ResolveAsync(hash, ct);
                if (entity is not null)
                    break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Download entity resolver {Resolver} failed for {Hash}",
                    resolver.GetType().Name, hash);
            }
        }

        _entities[hash] = new EntityCacheEntry(
            entity,
            now + (entity is null ? NegativeEntityCacheLifetime : EntityCacheLifetime));
        return entity;
    }

    static bool TargetsSubscribers(string key) => key switch
    {
        DownloadNotificationTopics.TorrentAdded => true,
        DownloadNotificationTopics.DownloadCompleted => true,
        DownloadNotificationTopics.DownloadReady => true,
        DownloadNotificationTopics.DownloadFailed => true,
        DownloadNotificationTopics.TorrentError => true,
        DownloadNotificationTopics.DownloadStalled => true,
        DownloadNotificationTopics.TorrentStriked => true,
        _ => false
    };

    sealed record EntityCacheEntry(MediaEntityRef? Entity, DateTime ExpiresAt);

    /// <summary>
    /// On first poll after startup, rebuild the known-torrent baseline from the event log so
    /// existing torrents are not re-announced and completed ones do not re-fire.
    /// </summary>
    async Task HydrateAsync(CancellationToken ct)
    {
        if (_hydrated)
            return;

        try
        {
            IReadOnlyList<SystemEvent> recent = await eventStore.QueryAsync(new SystemEventQuery
            {
                Category = EventCategory.Download,
                Limit = 5000
            }, ct);

            // The query returns newest first, so the most recent event per torrent decides whether
            // it is known. A removal is terminal: older Added and Progress events must not resurrect
            // the torrent.
            HashSet<string> decided = new(StringComparer.OrdinalIgnoreCase);

            foreach (SystemEvent e in recent)
            {
                if (string.IsNullOrEmpty(e.Source))
                    continue;

                // Ignore anything older than the newest event we already saw for this torrent.
                if (!decided.Add(e.Source))
                    continue;

                // The newest event is a removal: the torrent is gone, so leave it out entirely.
                if (e.EventType == DownloadEventType.Removed)
                    continue;

                TorrentSnapshot? hydrated = TryParseSnapshot(e.Data);
                if (hydrated is null)
                {
                    hydrated = new TorrentSnapshot
                    {
                        Name = e.SourceName ?? e.Source,
                        State = e.EventType == DownloadEventType.Completed ? TorrentState.Seeding : TorrentState.Downloading,
                        ProgressBucket = e.EventType == DownloadEventType.Completed ? 10 : 0,
                        Completed = e.EventType == DownloadEventType.Completed
                    };
                }

                _snapshots[e.Source] = hydrated;
            }

            _hydrated = true;
            logger.LogDebug("Download event monitor hydrated {Count} torrent baselines", _snapshots.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // If hydration fails we still mark hydrated to avoid retrying forever; worst case
            // some torrents produce a duplicate Added event.
            _hydrated = true;
            logger.LogDebug(ex, "Download event monitor could not hydrate from event log");
        }
    }

    async Task PersistAsync(List<SystemEventRecord> events, CancellationToken ct)
    {
        try
        {
            await eventStore.RecordAsync(events, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to persist {Count} download events", events.Count);
        }
    }

    static TorrentSnapshot? TryParseSnapshot(string? data)
    {
        if (string.IsNullOrWhiteSpace(data))
            return null;

        try
        {
            return JsonSerializer.Deserialize<TorrentSnapshot>(data);
        }
        catch
        {
            return null;
        }
    }

    void CollectChanges(TorrentSnapshot previous, TorrentItem current, DateTime now, List<SystemEventRecord> events)
    {
        bool completed = current.Progress >= 1.0;
        if (completed && !previous.Completed)
        {
            events.Add(New(current, DownloadEventType.Completed, EventSeverity.Success,
                $"Torrent completed: {current.Name}", now));
        }

        // Progress at 10% steps (only while still downloading).
        int bucket = (int)Math.Floor(current.Progress / ProgressStep);
        if (!completed && bucket > previous.ProgressBucket && bucket > 0)
        {
            int percent = Math.Min(100, bucket * 10);
            events.Add(New(current, DownloadEventType.Progress, EventSeverity.Info,
                $"Downloading {current.Name}: {percent}%", now));
        }

        if (current.State == TorrentState.Error && previous.State != TorrentState.Error)
        {
            events.Add(New(current, DownloadEventType.Error, EventSeverity.Error,
                $"Torrent errored: {current.Name}", now));
        }
        else if (current.State != previous.State)
        {
            switch (current.State)
            {
                case TorrentState.Paused:
                    events.Add(New(current, DownloadEventType.Paused, EventSeverity.Info,
                        $"Torrent paused: {current.Name}", now));
                    break;
                case TorrentState.Downloading when previous.State == TorrentState.Paused:
                    events.Add(New(current, DownloadEventType.Resumed, EventSeverity.Info,
                        $"Torrent resumed: {current.Name}", now));
                    break;
                case TorrentState.Downloading:
                    events.Add(New(current, DownloadEventType.StateChanged, EventSeverity.Info,
                        $"Torrent started: {current.Name}", now));
                    break;
                case TorrentState.Seeding:
                    events.Add(New(current, DownloadEventType.StateChanged, EventSeverity.Success,
                        $"Torrent seeding: {current.Name}", now));
                    break;
                default:
                    events.Add(New(current, DownloadEventType.StateChanged, EventSeverity.Info,
                        $"Torrent state changed to {current.State}: {current.Name}", now));
                    break;
            }
        }

        bool stalled = TorrentHealth.IsStalled(current);
        if (stalled && !previous.StalledReported)
        {
            events.Add(New(current, DownloadEventType.Stalled, EventSeverity.Warning,
                $"Torrent stalled (no peers): {current.Name}", now));
        }
    }

    static TorrentSnapshot Snapshot(TorrentItem torrent, bool stalledReported) => new()
    {
        Name = torrent.Name,
        State = torrent.State,
        ProgressBucket = (int)Math.Floor(torrent.Progress / ProgressStep),
        Completed = torrent.Progress >= 1.0,
        StalledReported = stalledReported
    };

    static SystemEventRecord New(TorrentItem torrent, string type, EventSeverity severity, string message, DateTime now)
        => New(torrent.Hash, torrent.Name, type, severity, message, now,
            Serialize(Snapshot(torrent, TorrentHealth.IsStalled(torrent))),
            BuildNotification(type, torrent, severity, now));

    static SystemEventRecord New(
        string hash,
        string name,
        string type,
        EventSeverity severity,
        string message,
        DateTime now,
        string? data = null,
        NotificationMessage? notification = null) => new()
        {
            Category = EventCategory.Download,
            EventType = type,
            Severity = severity,
            Message = message,
            Source = hash,
            SourceName = name,
            Data = data,
            Notification = notification,
            Timestamp = now
        };

    /// <summary>Maps a download event type to its notification key, or an empty string for audit-only events.</summary>
    static string NotificationKeyFor(string type) => type switch
    {
        DownloadEventType.Added => DownloadNotificationTopics.TorrentAdded,
        DownloadEventType.Progress => DownloadNotificationTopics.DownloadProgress,
        DownloadEventType.Completed => DownloadNotificationTopics.DownloadCompleted,
        DownloadEventType.Error => DownloadNotificationTopics.TorrentError,
        DownloadEventType.Stalled => DownloadNotificationTopics.DownloadStalled,
        DownloadEventType.Paused => DownloadNotificationTopics.DownloadPaused,
        DownloadEventType.Resumed => DownloadNotificationTopics.DownloadResumed,
        DownloadEventType.Removed => DownloadNotificationTopics.TorrentRemoved,
        DownloadEventType.Struck => DownloadNotificationTopics.TorrentStriked,
        _ => ""
    };

    /// <summary>
    /// Builds the notification body for a topic. The notification title is not repeated.
    /// </summary>
    static string NotificationBodyFor(string key, string name, double? progress = null) => key switch
    {
        DownloadNotificationTopics.TorrentAdded => $"Added torrent {name}",
        DownloadNotificationTopics.TorrentRemoved => $"Removed torrent {name}",
        DownloadNotificationTopics.DownloadCompleted => $"Finished downloading {name}",
        DownloadNotificationTopics.TorrentError => $"Failed to download {name}",
        DownloadNotificationTopics.DownloadStalled => $"No peers for {name}",
        DownloadNotificationTopics.DownloadPaused => $"Paused torrent {name}",
        DownloadNotificationTopics.DownloadResumed => $"Resumed torrent {name}",
        DownloadNotificationTopics.TorrentStriked => $"Removed torrent {name}",
        DownloadNotificationTopics.DownloadProgress => progress is { } value
            ? $"Downloading {name}: {(int)Math.Floor(value * 100)}%"
            : $"Downloading {name}",
        _ => name
    };

    static NotificationMessage? BuildNotification(
        string type, TorrentItem torrent, EventSeverity severity, DateTime now)
    {
        string key = NotificationKeyFor(type);
        if (key.Length == 0)
            return null;

        return NotificationFor(key, NotificationTitles.TitleFor(key), torrent.Name, torrent.Hash,
            NotificationBodyFor(key, torrent.Name, torrent.Progress), severity, now, TorrentFields(torrent));
    }

    static NotificationMessage NotificationFor(
        string key,
        string title,
        string sourceName,
        string source,
        string message,
        EventSeverity severity,
        DateTime now,
        IReadOnlyList<NotificationField>? fields = null) => new()
        {
            Key = key,
            Title = title,
            Body = message,
            Severity = severity,
            Source = source,
            SourceName = sourceName,
            Timestamp = now,
            Fields = fields ?? []
        };

    static IReadOnlyList<NotificationField> TorrentFields(TorrentItem torrent) =>
    [
        new("Progress", $"{(int)Math.Floor(torrent.Progress * 100)}%"),
        new("Size", FormatBytes(torrent.Size)),
        new("Ratio", torrent.Ratio.ToString("F2"))
    ];

    static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.##} {units[unit]}";
    }

    static string Serialize(TorrentSnapshot snapshot) => JsonSerializer.Serialize(snapshot);
}

/// <summary>Human-readable titles for notification keys produced by the download pipeline.</summary>
internal static class NotificationTitles
{
    public static string TitleFor(string key) => key switch
    {
        DownloadNotificationTopics.TorrentAdded => "Torrent added",
        DownloadNotificationTopics.DownloadProgress => "Download progress",
        DownloadNotificationTopics.DownloadCompleted => "Download completed",
        DownloadNotificationTopics.TorrentError => "Torrent error",
        DownloadNotificationTopics.DownloadStalled => "Download stalled",
        DownloadNotificationTopics.DownloadPaused => "Download paused",
        DownloadNotificationTopics.DownloadResumed => "Download resumed",
        DownloadNotificationTopics.TorrentRemoved => "Torrent removed",
        DownloadNotificationTopics.TorrentStriked => "Torrent removed",
        _ => "Download event"
    };
}
