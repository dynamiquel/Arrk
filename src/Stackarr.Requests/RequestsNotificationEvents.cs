using Stackarr.Core.Notifications;

namespace Stackarr.Requests;

/// <summary>Hierarchical notification topics for the request system.</summary>
public static class RequestsNotificationTopics
{
    public const string New = "media.requests.new";
    public const string Approved = "media.requests.approved";
    public const string Denied = "media.requests.denied";
    public const string Blacklisted = "media.requests.blacklisted";
    public const string Available = "media.requests.available";
}

/// <summary>Contributes request-system notification events.</summary>
public sealed class RequestsNotificationEvents : INotificationEventProvider
{
    public IReadOnlyList<NotificationEventDefinition> Events { get; } =
    [
        new(RequestsNotificationTopics.New, true, "A user requested media (admins)", "🎟️", 0x9B59B6),
        new(RequestsNotificationTopics.Approved, true, "A requested title was approved", "✅", 0x2ECC71),
        new(RequestsNotificationTopics.Denied, true, "A requested title was denied", "⛔", 0xE74C3C),
        new(RequestsNotificationTopics.Blacklisted, true, "A requested title was blacklisted", "🚫", 0xE67E22),
        new(RequestsNotificationTopics.Available, true, "A requested title became available", "🎬", 0x3498DB),
    ];

    public IReadOnlyList<NotificationGroupDefinition> Groups { get; } =
    [
        new("media.requests", "Requests", "Media request activity"),
    ];
}
