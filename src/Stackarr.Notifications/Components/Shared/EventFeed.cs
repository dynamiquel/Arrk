using MudBlazor;
using Stackarr.Core.Events;
using Stackarr.Core.Models;
using Stackarr.Core.Notifications;

namespace Stackarr.Notifications.Components.Shared;

/// <summary>
/// A single row in the unified event feed. Wraps a <see cref="SystemEvent"/> and surfaces the
/// notification topic (when the event carried a notification) so the UI can treat a notification
/// as a special kind of event and filter by hierarchical topic.
/// </summary>
public sealed record EventFeedItem
{
    public long Id { get; init; }
    public DateTime Timestamp { get; init; }
    public string Category { get; init; } = "";
    public string EventType { get; init; } = "";
    public string Message { get; init; } = "";
    public string Severity { get; init; } = "Info";
    public string? Topic { get; init; }
    public string? Source { get; init; }
    public string? SourceName { get; init; }

    /// <summary>
    /// True when this event also produced (or intended to produce) a notification. Job lifecycle
    /// events carry a topic for filtering but are audit-only, so they are never notifications.
    /// </summary>
    public bool IsNotification => !string.IsNullOrWhiteSpace(Topic) && Category != EventCategory.Job;

    public static EventFeedItem FromEvent(SystemEvent systemEvent) => new()
    {
        Id = systemEvent.Id,
        Timestamp = systemEvent.Timestamp,
        Category = systemEvent.Category,
        EventType = systemEvent.EventType,
        Message = systemEvent.Message,
        Severity = systemEvent.Severity,
        Topic = systemEvent.Topic,
        Source = systemEvent.Source,
        SourceName = systemEvent.SourceName
    };
}

/// <summary>
/// One of the friendly, human-readable event filters shown as an on/off toggle on the events page.
/// Each filter maps a plain-English label ("Downloads", "System", "Media", ...) onto the underlying
/// category and topic namespace, so people never have to know the topic syntax to find things.
/// </summary>
public sealed record EventCategoryFilter(string Id, string Label, string Icon, string Title);

/// <summary>Pure matching logic for the unified event feed. Kept free of UI so it can be unit tested.</summary>
public static class EventFeedFilter
{
    public const string Downloads = "downloads";
    public const string Media = "media";
    public const string System = "system";
    public const string Jobs = "jobs";
    public const string Health = "health";
    public const string Notifications = "notifications";

    /// <summary>
    /// The curated toggle filters presented to the user, in display order. Every filter is enabled
    /// by default; the events page lets people switch individual ones off (Jobs is the usual
    /// candidate because background jobs are noisy).
    /// </summary>
    public static IReadOnlyList<EventCategoryFilter> Filters { get; } =
    [
        new(Downloads, "Downloads", Icons.Material.Filled.CloudDownload, "Show download events and download notifications"),
        new(Media, "Media", Icons.Material.Filled.Movie, "Show media library events and media notifications"),
        new(System, "System", Icons.Material.Filled.Memory, "Show general system events"),
        new(Jobs, "Jobs", Icons.Material.Filled.Schedule, "Show background job audit events (noisy)"),
        new(Health, "Health", Icons.Material.Filled.MonitorHeart, "Show health, storage and service events"),
        new(Notifications, "Notifications", Icons.Material.Filled.NotificationsActive, "Show events that produced a notification")
    ];

    /// <summary>A fresh set containing every toggle filter, for the page's default "all on" state.</summary>
    public static HashSet<string> CreateDefaultSelection() =>
        Filters.Select(f => f.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when the item belongs to the friendly category identified by <paramref name="filterId"/>.</summary>
    public static bool MatchesCategory(EventFeedItem item, string? filterId) => filterId switch
    {
        Downloads => item.Category == EventCategory.Download || TopicMatches("downloads", item.Topic),
        Media => item.Category == EventCategory.Media || TopicMatches("media", item.Topic),
        System => item.Category == EventCategory.System || TopicMatches("system", item.Topic),
        Jobs => item.Category == EventCategory.Job,
        Health => item.Category is EventCategory.Health or EventCategory.Storage or EventCategory.Service
            || TopicMatches("system.health", item.Topic),
        Notifications => item.IsNotification,
        _ => true
    };

    /// <summary>
    /// True when at least one enabled category matches the item. An event that belongs to several
    /// categories (e.g. a notification also has a downloads/media topic) stays visible while any of
    /// them is enabled. With no toggles enabled the feed is empty; events that match no known
    /// category stay visible so a new category cannot silently disappear.
    /// </summary>
    public static bool IsVisible(EventFeedItem item, IReadOnlySet<string> enabledFilterIds)
    {
        if (enabledFilterIds.Count == 0)
            return false;

        bool matchedKnownFilter = false;
        foreach (EventCategoryFilter filter in Filters)
        {
            if (!MatchesCategory(item, filter.Id))
                continue;

            matchedKnownFilter = true;
            if (enabledFilterIds.Contains(filter.Id))
                return true;
        }

        return !matchedKnownFilter;
    }

    static bool TopicMatches(string prefix, string? topic) =>
        topic is not null && TopicFilter.Matches(prefix, topic);
}
