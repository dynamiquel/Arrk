namespace Stackarr.Core.Notifications;

/// <summary>
/// Delivery-only topic filters for a single channel. Unlike the global
/// <see cref="Configuration.NotificationConfig.Topics"/> toggles, an exclude list does not change
/// whether an alert exists: the alert is still recorded in the in-app inbox, but the transport
/// skips it. Patterns use the same hierarchical syntax as <see cref="TopicFilter"/>.
/// </summary>
public static class NotificationFilter
{
    /// <summary>
    /// True when <paramref name="topic"/> matches any of the configured <paramref name="patterns"/>.
    /// Blank patterns are ignored; an empty list excludes nothing.
    /// </summary>
    public static bool IsExcluded(IEnumerable<string>? patterns, string? topic)
    {
        if (patterns is null || string.IsNullOrWhiteSpace(topic))
            return false;

        foreach (string pattern in patterns)
        {
            if (!string.IsNullOrWhiteSpace(pattern) && TopicFilter.Matches(pattern, topic))
                return true;
        }

        return false;
    }
}
