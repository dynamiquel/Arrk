using Stackarr.Media.Abstractions;

namespace Stackarr.Core.Notifications;

/// <summary>
/// A user's interest in one media entity. Subscriptions are the opt-in layer that keeps direct
/// channels quiet: only subscribed users receive an entity event, and each subscription can
/// narrow the categories it cares about.
/// </summary>
public class NotificationSubscription
{
    public long Id { get; set; }

    public int UserId { get; set; }

    /// <summary>Media service key, e.g. <c>tv</c>.</summary>
    public string ProviderKey { get; set; } = "";

    /// <summary>Broad media domain.</summary>
    public MediaDomain Domain { get; set; }

    /// <summary>Stable module-owned entity key.</summary>
    public string ItemKey { get; set; } = "";

    /// <summary>True when child entities (episodes under a season, seasons under a series) also match.</summary>
    public bool IncludeDescendants { get; set; }

    /// <summary>Where the subscription came from: <c>request</c>, <c>manual</c>, later <c>watchlist</c>.</summary>
    public string Source { get; set; } = "manual";

    /// <summary>Optional source row id, e.g. the request id.</summary>
    public string? SourceId { get; set; }

    public string Title { get; set; } = "";
    public string? Subtitle { get; set; }
    public string? PosterUrl { get; set; }

    public bool NotifyStatus { get; set; } = true;
    public bool NotifyDownload { get; set; } = true;
    public bool NotifyNewContent { get; set; } = true;
    public bool NotifyProblems { get; set; } = true;
    public bool NotifyLifecycle { get; set; } = true;

    public DateTime CreatedAt { get; set; }
    public DateTime? LastNotifiedAt { get; set; }

    /// <summary>Whether a topic category is enabled on this subscription.</summary>
    public bool Allows(string topic) => NotificationSubscriptionTopics.Allows(this, topic);

    /// <summary>Whether this subscription matches the entity (exact, descendant or ancestor).</summary>
    public bool Matches(MediaEntityRef entity)
    {
        if (!string.Equals(ProviderKey, entity.ProviderKey, StringComparison.OrdinalIgnoreCase))
            return false;

        return MatchesKey(entity.ItemKey, entity.Ancestors);
    }

    public bool MatchesKey(string itemKey, IReadOnlyList<string> ancestors)
    {
        if (string.Equals(ItemKey, itemKey, StringComparison.OrdinalIgnoreCase))
            return true;

        if (IncludeDescendants && IsDescendantOf(ItemKey, itemKey))
            return true;

        foreach (string ancestor in ancestors)
        {
            if (string.Equals(ItemKey, ancestor, StringComparison.OrdinalIgnoreCase))
                return true;

            if (IncludeDescendants && IsDescendantOf(ItemKey, ancestor))
                return true;
        }

        return false;
    }

    static bool IsDescendantOf(string parentKey, string candidate) =>
        candidate.Length > parentKey.Length &&
        candidate.StartsWith(parentKey, StringComparison.OrdinalIgnoreCase) &&
        candidate[parentKey.Length] == ':';
}

/// <summary>Category switches applied when subscribing.</summary>
public sealed record NotificationSubscriptionOptions
{
    public bool IncludeDescendants { get; init; }

    public bool Status { get; init; } = true;
    public bool Download { get; init; } = true;
    public bool NewContent { get; init; } = true;
    public bool Problems { get; init; } = true;
    public bool Lifecycle { get; init; } = true;
}

/// <summary>The user-facing category a subscription event belongs to.</summary>
public enum NotificationSubscriptionCategory
{
    Status = 0,
    Download = 1,
    NewContent = 2,
    Problems = 3,
    Lifecycle = 4
}

/// <summary>Maps notification topics to the subscription category a user can toggle.</summary>
public static class NotificationSubscriptionTopics
{
    /// <summary>The category for a topic, or null when subscriptions never target it.</summary>
    public static NotificationSubscriptionCategory? CategoryOf(string? topic)
    {
        if (string.IsNullOrWhiteSpace(topic))
            return null;

        if (topic.StartsWith("media.requests.", StringComparison.OrdinalIgnoreCase))
            return NotificationSubscriptionCategory.Status;

        return topic switch
        {
            "downloads.progress" => null,
            "downloads.torrent.added" or "downloads.completed" or "downloads.ready"
                => NotificationSubscriptionCategory.Download,
            "downloads.imported" or "media.tv.added" or "media.tv.upgraded"
                or "media.tv.episode.grabbed" or "media.tv.movie.grabbed"
                or "downloads.soulseek.grabbed" => NotificationSubscriptionCategory.NewContent,
            "downloads.failed" or "downloads.torrent.error" or "downloads.torrent.stalled"
                or "downloads.torrent.slow" or "downloads.torrent.struck"
                or "downloads.music.album.failed" => NotificationSubscriptionCategory.Problems,
            _ when topic.StartsWith("media.lifecycle.", StringComparison.OrdinalIgnoreCase)
                => NotificationSubscriptionCategory.Lifecycle,
            "media.tv.about_to_delete" or "media.tv.deleted"
                => NotificationSubscriptionCategory.Lifecycle,
            _ => null
        };
    }

    public static bool Allows(NotificationSubscription subscription, string topic) =>
        CategoryOf(topic) switch
        {
            NotificationSubscriptionCategory.Status => subscription.NotifyStatus,
            NotificationSubscriptionCategory.Download => subscription.NotifyDownload,
            NotificationSubscriptionCategory.NewContent => subscription.NotifyNewContent,
            NotificationSubscriptionCategory.Problems => subscription.NotifyProblems,
            NotificationSubscriptionCategory.Lifecycle => subscription.NotifyLifecycle,
            _ => false
        };

    /// <summary>True when the topic is an entity event that subscriptions can target.</summary>
    public static bool IsEntityTopic(string topic) => CategoryOf(topic) is not null;
}
