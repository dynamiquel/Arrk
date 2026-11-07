using Arrk.Notifications;

namespace Arrk.Tvarr;

/// <summary>Hierarchical notification topics for library video, series, movies and subtitles (owned by the TV module).</summary>
public static class MediaNotificationTopics
{
    public const string MediaAdded = "media.tv.added";
    public const string MediaUpgraded = "media.tv.upgraded";
    public const string MediaDeleted = "media.tv.deleted";
    public const string MediaWatched = "media.tv.watched";
    public const string MediaAboutToDelete = "media.tv.about_to_delete";
    public const string EpisodeGrabbed = "media.tv.episode.grabbed";
    public const string MovieGrabbed = "media.tv.movie.grabbed";
}

/// <summary>Contributes video/library notification events (TV series and movies share the media.tv namespace).</summary>
public sealed class MediaNotificationEvents : INotificationEventProvider
{
    public IReadOnlyList<NotificationEventDefinition> Events { get; } =
    [
        new(MediaNotificationTopics.MediaAdded, true, "New media was added to the library", "🎬", 0x9B59B6),
        new(MediaNotificationTopics.MediaUpgraded, true, "A media file was upgraded", "⬆️", 0x3498DB),
        new(MediaNotificationTopics.MediaDeleted, true, "Media was deleted from the library", "🗑️", 0xE74C3C),
        new(MediaNotificationTopics.MediaWatched, false, "A user finished watching media (noisy)", "👀", 0x1ABC9C),
        new(MediaNotificationTopics.MediaAboutToDelete, true, "Media is scheduled for deletion", "⏳", 0xE67E22),
        new(MediaNotificationTopics.EpisodeGrabbed, true, "An episode was grabbed for download", "📺", 0x9B59B6),
        new(MediaNotificationTopics.MovieGrabbed, true, "A movie was grabbed for download", "🎬", 0x9B59B6),
    ];

    public IReadOnlyList<NotificationGroupDefinition> Groups { get; } =
    [
        new("media.tv", "TV & Movies", "Library changes"),
        new("media.tv.episode", "Episodes", "Episode grabs"),
        new("media.tv.movie", "Movies", "Movie grabs"),
    ];
}
