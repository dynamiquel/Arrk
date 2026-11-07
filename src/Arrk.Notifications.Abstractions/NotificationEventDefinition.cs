namespace Arrk.Notifications;

/// <summary>
/// Describes a single notifiable event using a hierarchical topic. Modules define
/// these for the domain they own and register them through <see cref="INotificationEventProvider"/>.
/// </summary>
/// <param name="Topic">Stable dotted topic, e.g. <c>system.health.service.down</c>.</param>
/// <param name="EnabledByDefault">Whether alerts fire when the config has no matching toggle.</param>
/// <param name="Description">Human-readable description used by the config/settings UI.</param>
/// <param name="Emoji">Emoji prefix used by rich channels such as Discord.</param>
/// <param name="Color">Decimal RGB accent colour used by rich channels.</param>
public sealed record NotificationEventDefinition(
    string Topic,
    bool EnabledByDefault,
    string Description,
    string Emoji,
    int Color)
{
    /// <summary>Parent group topic, e.g. <c>system.health.service</c>. Equal to the topic when there is no parent.</summary>
    public string Group => Topic.Contains('.') ? Topic[..Topic.LastIndexOf('.')] : Topic;

    /// <summary>Top-level domain, e.g. <c>system</c>, <c>downloads</c>, <c>media</c>.</summary>
    public string Domain => Topic.Contains('.') ? Topic[..Topic.IndexOf('.')] : Topic;
}

/// <summary>
/// A meaningful, user-toggleable group in the topic tree. Groups are not events themselves; they
/// let the settings UI present a handful of toggles instead of every event.
/// </summary>
public sealed record NotificationGroupDefinition(string Topic, string DisplayName, string Description);
