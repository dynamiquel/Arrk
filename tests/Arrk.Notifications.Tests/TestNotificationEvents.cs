using Arrk.DownloadManagement;
using Arrk.Notifications;

namespace Arrk.Notifications.Tests;

/// <summary>Topics for the synthetic provider used to exercise the generic notification system.</summary>
public static class TestNotificationTopics
{
    public const string Info = "test.info";
    public const string Success = "test.success";
    public const string Warning = "test.warning";
    public const string Noisy = "test.noisy";
    public const string GroupAlpha = "test.group.alpha";
    public const string GroupBeta = "test.group.beta";
    public const string GroupNoisy = "test.group.noisy";
    public const string Media = "test.media";
}

/// <summary>
/// A small provider so the notification system can be tested without depending on any domain
/// module. The real per-domain catalogues are tested in their own module test projects.
/// </summary>
public sealed class TestNotificationEvents : INotificationEventProvider
{
    public IReadOnlyList<NotificationEventDefinition> Events { get; } =
    [
        new(TestNotificationTopics.Info, true, "Informational", "ℹ️", 0x3498DB),
        new(TestNotificationTopics.Success, true, "Succeeded", "✅", 0x2ECC71),
        new(TestNotificationTopics.Warning, true, "Warning", "⚠️", 0xE67E22),
        new(TestNotificationTopics.Noisy, false, "Noisy event", "🔔", 0x3498DB),
        new(TestNotificationTopics.GroupAlpha, true, "Alpha", "🅰️", 0x3498DB),
        new(TestNotificationTopics.GroupBeta, true, "Beta", "🅱️", 0x3498DB),
        new(TestNotificationTopics.GroupNoisy, false, "Noisy group member", "🔕", 0x95A5A6),
        new(TestNotificationTopics.Media, true, "Media-ish event", "🎬", 0x9B59B6),
    ];

    public IReadOnlyList<NotificationGroupDefinition> Groups { get; } =
    [
        new("test", "Test", "Synthetic events"),
        new("test.group", "Test group", "Synthetic grouped events"),
    ];
}

public static class TestCatalog
{
    /// <summary>Registry containing the synthetic provider plus Core's real download catalogue.</summary>
    public static NotificationEventRegistry Registry(params INotificationEventProvider[] extra) =>
        new([new TestNotificationEvents(), new DownloadNotificationEvents(), .. extra]);
}
