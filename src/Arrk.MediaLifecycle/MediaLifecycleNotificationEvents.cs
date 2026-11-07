using Arrk.Notifications;

namespace Arrk.MediaLifecycle;

/// <summary>Notification topics for staleness and automatic cleanup.</summary>
public static class MediaLifecycleTopics
{
    public const string AboutToBeStale = "media.lifecycle.about_to_be_stale";
    public const string Stale = "media.lifecycle.stale";
    public const string Deleted = "media.lifecycle.deleted";
    public const string ScanFailed = "media.lifecycle.scan_failed";
}

public sealed class MediaLifecycleNotificationEvents : INotificationEventProvider
{
    public IReadOnlyList<NotificationEventDefinition> Events { get; } =
    [
        new(MediaLifecycleTopics.AboutToBeStale, true, "Media is approaching its stale threshold", "⏳", 0xE67E22),
        new(MediaLifecycleTopics.Stale, true, "Media has become stale and can be reclaimed", "🧹", 0xE74C3C),
        new(MediaLifecycleTopics.Deleted, true, "Stale media was automatically deleted", "🗑️", 0x95A5A6),
        new(MediaLifecycleTopics.ScanFailed, true, "The stale-media scan failed", "⚠️", 0xE74C3C),
    ];

    public IReadOnlyList<NotificationGroupDefinition> Groups { get; } =
    [
        new("media.lifecycle", "Media lifecycle", "Staleness warnings and automatic cleanup"),
    ];
}
