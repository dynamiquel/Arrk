namespace Stackarr.Core.Notifications;

/// <summary>
/// The aggregated catalogue of notification events and groups contributed by every module. Built
/// once from all registered <see cref="INotificationEventProvider"/>s and consumed by the
/// notification service and channels. Duplicate topics are a programming error and throw at
/// startup so drift is caught immediately.
/// </summary>
public sealed class NotificationEventRegistry
{
    readonly Dictionary<string, NotificationEventDefinition> _events;
    readonly Dictionary<string, NotificationGroupDefinition> _groups;

    public NotificationEventRegistry(IEnumerable<INotificationEventProvider> providers)
    {
        Dictionary<string, NotificationEventDefinition> events = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, NotificationGroupDefinition> groups = new(StringComparer.OrdinalIgnoreCase);

        foreach (INotificationEventProvider provider in providers)
        {
            foreach (NotificationEventDefinition definition in provider.Events)
            {
                if (!events.TryAdd(definition.Topic, definition))
                    throw new InvalidOperationException(
                        $"Duplicate notification topic '{definition.Topic}' contributed by {provider.GetType().Name}.");
            }

            foreach (NotificationGroupDefinition group in provider.Groups)
                groups.TryAdd(group.Topic, group);
        }

        _events = events;
        _groups = groups;
        Events = events.Values.OrderBy(e => e.Topic, StringComparer.Ordinal).ToList();
        Groups = groups.Values.OrderBy(g => g.Topic, StringComparer.Ordinal).ToList();
    }

    /// <summary>Every registered event, ordered by topic.</summary>
    public IReadOnlyList<NotificationEventDefinition> Events { get; }

    /// <summary>Every curated group, ordered by topic.</summary>
    public IReadOnlyList<NotificationGroupDefinition> Groups { get; }

    /// <summary>Looks up an event definition, or null when the topic is unknown.</summary>
    public NotificationEventDefinition? Get(string topic) => _events.GetValueOrDefault(topic);

    /// <summary>Whether a topic is part of the catalogue.</summary>
    public bool IsKnown(string topic) => _events.ContainsKey(topic);

    /// <summary>Whether a topic is enabled by default. Unknown topics are not notifiable.</summary>
    public bool DefaultEnabled(string topic) => Get(topic)?.EnabledByDefault ?? false;

    /// <summary>The parent group of a topic.</summary>
    public string GroupOf(string topic) => Get(topic)?.Group ?? topic;

    /// <summary>The top-level domain of a topic.</summary>
    public string DomainOf(string topic) => Get(topic)?.Domain ?? topic;

    /// <summary>All definitions whose topic equals or falls under a group prefix.</summary>
    public IReadOnlyList<NotificationEventDefinition> InGroup(string group) =>
        Events.Where(e =>
            e.Topic.Equals(group, StringComparison.OrdinalIgnoreCase) ||
            e.Topic.StartsWith(group + ".", StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>Human-readable name for a group, falling back to title-cased segments.</summary>
    public string DisplayName(string group)
    {
        if (_groups.TryGetValue(group, out NotificationGroupDefinition? known))
            return known.DisplayName;

        NotificationEventDefinition? definition = Get(group);
        if (definition is not null)
            return TitleCase(definition.Topic.Split('.').Last());

        return TitleCase(group.Split('.').Last());
    }

    /// <summary>Resolves the presentation style for a topic (emoji, accent colour, author, footer).</summary>
    public NotificationStyle StyleFor(string topic)
    {
        NotificationEventDefinition? definition = Get(topic);
        string group = GroupOf(topic);

        return new NotificationStyle
        {
            Emoji = definition?.Emoji,
            Color = definition?.Color,
            AuthorName = "Stackarr",
            FooterText = $"Stackarr • {DisplayName(group)}"
        };
    }

    /// <summary>Whether a topic should fire for the given config (topic prefix resolution).</summary>
    public bool IsEnabled(Configuration.NotificationConfig config, string topic) =>
        NotificationPolicy.IsEnabled(config, topic, this);

    static string TitleCase(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        return char.ToUpperInvariant(value[0]) + value[1..].Replace('_', ' ');
    }
}
