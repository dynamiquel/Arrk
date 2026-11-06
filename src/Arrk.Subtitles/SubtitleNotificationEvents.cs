using Arrk.Core.Notifications;

namespace Arrk.Subtitles;

public static class SubtitleNotificationTopics
{
    public const string SubtitleDownloaded = "media.tv.subtitle.downloaded";
}

/// <summary>Notification topics owned by the subtitles feature.</summary>
public sealed class SubtitleNotificationEvents : INotificationEventProvider
{
    public IReadOnlyList<NotificationEventDefinition> Events { get; } =
    [
        new(SubtitleNotificationTopics.SubtitleDownloaded, true, "A subtitle was downloaded", "💬", 0x9B59B6)
    ];

    public IReadOnlyList<NotificationGroupDefinition> Groups { get; } =
    [
        new("media.tv.subtitle", "Subtitles", "Subtitle downloads")
    ];
}
