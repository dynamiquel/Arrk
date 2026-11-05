using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Stackarr.Core;
using Stackarr.Core.Events;
using Stackarr.Core.Lifecycle;
using Stackarr.Core.Modules;
using Stackarr.Core.Notifications;
using Stackarr.Media.Abstractions;

namespace Stackarr.MediaLifecycle;

/// <summary>
/// Hourly staleness pass: announces items crossing the warning/stale thresholds (once per level),
/// and deletes stale items when the service's <c>autoCleanup</c> flag is on.
/// </summary>
public class StaleCleanupJob(
    MediaLifecycleService lifecycle,
    IDbContextFactory<StackarrDbContext> dbFactory,
    IEventStore events,
    TimeProvider time,
    ILogger<StaleCleanupJob> logger)
{
    public const string DefaultCron = "0 * * * *";

    [SingleFlight]
    [JobDisplayName("Stale Media Scan")]
    public async Task Execute(CancellationToken ct)
    {
        StaleMediaReport report;
        try
        {
            report = await lifecycle.GetReportAsync(forceRefresh: true, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Stale-media scan failed");
            return;
        }

        await using StackarrDbContext db = await dbFactory.CreateDbContextAsync(ct);
        List<LifecycleItemState> states = await db.Set<LifecycleItemState>().ToListAsync(ct);
        Dictionary<(string Provider, string Item), LifecycleItemState> byKey = states
            .ToDictionary(x => (x.ProviderKey, x.ItemKey));
        HashSet<(string Provider, string Item)> seen = [];
        DateTime now = time.GetUtcNow().UtcDateTime;

        foreach (StaleMediaItem item in report.Items)
        {
            (string, string) key = (item.ProviderKey, item.ItemKey);
            seen.Add(key);
            if (!byKey.TryGetValue(key, out LifecycleItemState? state))
            {
                state = new LifecycleItemState
                {
                    ProviderKey = item.ProviderKey,
                    ItemKey = item.ItemKey,
                    FirstSeenAt = now
                };
                db.Set<LifecycleItemState>().Add(state);
                byKey[key] = state;
            }

            bool changed = state.Level != item.Level || state.Id == 0;
            state.ItemType = item.Item.ItemType;
            state.Title = item.Item.Title;
            state.Level = item.Level;
            state.UpdatedAt = now;

            if (changed && item.Level == StaleLevel.Expiring)
                await NotifyAsync(item, MediaLifecycleTopics.AboutToBeStale, ct);
            else if (changed && item.Level == StaleLevel.Stale)
                await NotifyAsync(item, MediaLifecycleTopics.Stale, ct);

            StaleThresholds thresholds = report.Thresholds.TryGetValue(item.ProviderKey, out StaleThresholds? limits)
                ? limits
                : StaleThresholds.Default;

            if (item.IsStale && thresholds.AutoCleanup)
            {
                ManagedMediaDeleteResult result = await lifecycle.DeleteAsync(item.ProviderKey, item.ItemKey, ct);
                if (result.Success)
                {
                    await NotifyDeletedAsync(item, result.BytesFreed, ct);
                    db.Set<LifecycleItemState>().Remove(state);
                    byKey.Remove(key);
                    seen.Remove(key);
                }
                else
                {
                    logger.LogWarning(
                        "Could not auto-delete stale {Service} item {Title}: {Message}",
                        item.ProviderKey, item.Item.Title, result.Message);
                }
            }
        }

        foreach (LifecycleItemState staleState in states)
        {
            if (!seen.Contains((staleState.ProviderKey, staleState.ItemKey)))
                db.Set<LifecycleItemState>().Remove(staleState);
        }

        await db.SaveChangesAsync(ct);
    }

    async Task NotifyAsync(StaleMediaItem item, string topic, CancellationToken ct)
    {
        string service = ServiceLabel(item.ProviderKey);
        string size = FormatBytes(item.Item.SizeOnDisk);
        string when = item.DaysRemaining is { } days
            ? $"{Math.Max(0, days):0.#} days"
            : "unknown";

        string title = topic == MediaLifecycleTopics.AboutToBeStale
            ? $"Leaving soon: {item.Item.Title}"
            : $"Stale media: {item.Item.Title}";

        string body = topic == MediaLifecycleTopics.AboutToBeStale
            ? $"{service} · {size} · becomes stale in {when}{(item.IsBookmarked ? $" (kept in {item.BookmarkLabel ?? "a list"})" : "")}"
            : $"{service} · {size} · not consumed for {item.AgeDays:0.#} days and can be reclaimed";

        MediaEntityRef entity = ToEntity(item);
        await events.RecordAsync(new SystemEventRecord
        {
            Category = EventCategory.Media,
            EventType = topic,
            Topic = topic,
            Severity = topic == MediaLifecycleTopics.AboutToBeStale ? EventSeverity.Warning : EventSeverity.Warning,
            Message = body,
            Source = item.ProviderKey,
            SourceName = item.Item.Title,
            Notification = new NotificationMessage
            {
                Key = topic,
                Title = title,
                Body = body,
                Severity = EventSeverity.Warning,
                Url = "/stale",
                Source = item.ProviderKey,
                SourceName = item.Item.Title,
                Timestamp = time.GetUtcNow().UtcDateTime,
                Entity = entity,
                Audience = NotificationAudience.Subscribers,
                Fields =
                [
                    new("Service", service),
                    new("Size", size),
                    new("Last consumed", item.LastConsumedAt is { } last ? $"{last:yyyy-MM-dd}" : "Never"),
                    new(item.DaysRemaining is null ? "Threshold" : "Days remaining", item.DaysRemaining is null ? "—" : when),
                    new("Bookmarked", item.IsBookmarked ? item.BookmarkLabel ?? "Yes" : "No")
                ]
            },
            Timestamp = time.GetUtcNow().UtcDateTime
        }, ct);

        logger.LogInformation("Staleness alert {Topic} for {Title}", topic, item.Item.Title);
    }

    async Task NotifyDeletedAsync(StaleMediaItem item, long bytesFreed, CancellationToken ct)
    {
        string service = ServiceLabel(item.ProviderKey);
        string size = FormatBytes(bytesFreed);
        MediaEntityRef entity = ToEntity(item);
        await events.RecordAsync(new SystemEventRecord
        {
            Category = EventCategory.Media,
            EventType = MediaLifecycleTopics.Deleted,
            Topic = MediaLifecycleTopics.Deleted,
            Severity = EventSeverity.Success,
            Message = $"Deleted stale {service} item {item.Item.Title}; reclaimed {size}",
            Source = item.ProviderKey,
            SourceName = item.Item.Title,
            Notification = new NotificationMessage
            {
                Key = MediaLifecycleTopics.Deleted,
                Title = $"Reclaimed {size}",
                Body = $"Deleted stale {service} item \"{item.Item.Title}\".",
                Severity = EventSeverity.Success,
                Url = "/stale",
                Source = item.ProviderKey,
                SourceName = item.Item.Title,
                Timestamp = time.GetUtcNow().UtcDateTime,
                Entity = entity,
                Audience = NotificationAudience.Subscribers,
                Fields =
                [
                    new("Service", service),
                    new("Space saved", size),
                    new("Last consumed", item.LastConsumedAt is { } last ? $"{last:yyyy-MM-dd}" : "Never")
                ]
            },
            Timestamp = time.GetUtcNow().UtcDateTime
        }, ct);
    }

    static MediaEntityRef ToEntity(StaleMediaItem item)
    {
        string? parent = item.ParentItemKey;
        IReadOnlyList<string> ancestors = string.IsNullOrWhiteSpace(parent) ? [] : [parent];
        return new MediaEntityRef(
            item.ProviderKey,
            item.Item.Domain,
            item.ItemKey,
            item.Item.Title,
            item.Item.Subtitle,
            item.Item.ImageUrl,
            ParentItemKey: parent,
            AncestorItemKeys: ancestors,
            Url: "/stale");
    }

    static string ServiceLabel(string providerKey) => providerKey switch
    {
        "tv" => "TV",
        "music" => "Music",
        _ => providerKey
    };

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        double value = Math.Max(0, bytes);
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.#} {units[unit]}";
    }
}
