namespace Arrk.Notifications;

/// <summary>
/// Pure decision logic for whether a notification topic should fire, given the user's
/// <see cref="NotificationConfig"/> and the aggregated event registry. Kept free of I/O so it can
/// be unit tested exhaustively.
/// </summary>
public static class NotificationPolicy
{
    /// <summary>
    /// Resolves the effective enabled state for a topic using hierarchical pattern matching: the
    /// most specific configured pattern wins. Keys are either a prefix or a wildcard pattern
    /// (<c>+</c> matches one level, <c>#</c>/<c>*</c> match any depth, as in <see cref="TopicFilter"/>).
    /// Examples:
    /// <list type="bullet">
    /// <item><description><c>system.health: false</c> disables every health event.</description></item>
    /// <item><description><c>downloads.*: false</c> disables every download event.</description></item>
    /// <item><description><c>system.health.service.down: true</c> re-enables one event under a disabled group.</description></item>
    /// <item><description>No matching pattern → the catalogue default applies.</description></item>
    /// </list>
    /// Unknown topics are never notifiable.
    /// </summary>
    public static bool IsEnabled(
        NotificationConfig config,
        string topic,
        NotificationEventRegistry registry)
    {
        NotificationEventDefinition? definition = registry.Get(topic);
        if (definition is null)
            return false;

        string? bestMatch = null;
        foreach (string pattern in config.Topics.Keys)
        {
            if (string.IsNullOrWhiteSpace(pattern) || !TopicFilter.Matches(pattern, definition.Topic))
                continue;

            if (bestMatch is null || TopicFilter.IsMoreSpecific(pattern, bestMatch))
                bestMatch = pattern;
        }

        if (bestMatch is not null)
            return config.Topics[bestMatch];

        return definition.EnabledByDefault;
    }

    /// <summary>True when <paramref name="prefix"/> equals the topic or is a parent segment of it.</summary>
    public static bool Matches(string prefix, string topic) =>
        topic.Equals(prefix, StringComparison.OrdinalIgnoreCase)
        || topic.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase);
}
