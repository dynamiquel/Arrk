using Stackarr.Core.Notifications;

namespace Stackarr.DownloadManagement;

/// <summary>Hierarchical notification topics for storage health (owned by the download-management module).</summary>
public static class StorageNotificationTopics
{
    public const string StorageLow = "system.health.storage.low";
    public const string StorageCritical = "system.health.storage.critical";
    public const string StorageCleanup = "system.health.storage.cleanup";
}

/// <summary>Contributes storage-health notification events.</summary>
public sealed class StorageNotificationEvents : INotificationEventProvider
{
    public IReadOnlyList<NotificationEventDefinition> Events { get; } =
    [
        new(StorageNotificationTopics.StorageLow, true, "Storage crossed the warning threshold", "⚠️", 0xE67E22),
        new(StorageNotificationTopics.StorageCritical, true, "Storage crossed the critical threshold", "🚨", 0xE74C3C),
        new(StorageNotificationTopics.StorageCleanup, true, "Seeding torrents were cleaned up for storage", "🧹", 0x3498DB),
    ];

    public IReadOnlyList<NotificationGroupDefinition> Groups { get; } =
    [
        new("system", "System", "Stackarr and infrastructure events"),
        new("system.health", "Health", "Service, storage and network health"),
        new("system.health.storage", "Storage", "Disk capacity warnings and cleanup"),
    ];
}
