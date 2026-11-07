namespace Arrk.Notifications;

/// <summary>
/// Resolves which webhook URL a notification should be delivered to. A channel has one default URL
/// plus an optional map of per-category overrides keyed by a notification topic pattern (so system
/// events, new media, music and requests can each go to their own Discord channel/webhook). Patterns
/// use the same syntax as <see cref="TopicFilter"/>: a bare key acts as a prefix, <c>+</c> matches one
/// level, and <c>#</c> or <c>*</c> matches any depth (<c>media.tv.#</c>, <c>downloads.*</c>). The most
/// specific matching pattern wins; unset or blank entries fall back to the default URL.
/// </summary>
public static class WebhookRouting
{
    /// <summary>
    /// Global destinations use topic routing; roles use only their explicit endpoint. Personal
    /// destinations and unmapped roles never fall back to a global endpoint. Endpoint access
    /// control is the operator's responsibility, not something a webhook can enforce.
    /// </summary>
    public static string? Resolve(
        NotificationRecipient recipient,
        IReadOnlyDictionary<string, string> roleRouting,
        IReadOnlyDictionary<string, string> routing,
        string? defaultUrl,
        string topic)
    {
        if (recipient.UserId is not null)
            return null;
        if (recipient.Role is { } role)
        {
            string? url = roleRouting.FirstOrDefault(entry =>
                string.Equals(entry.Key, role, StringComparison.OrdinalIgnoreCase)).Value;
            return string.IsNullOrWhiteSpace(url) ? null : url.Trim();
        }
        return Resolve(routing, defaultUrl, topic);
    }

    /// <summary>
    /// Returns the URL a notification for <paramref name="topic"/> should be delivered to, or null
    /// when neither an override nor the default is configured.
    /// </summary>
    public static string? Resolve(
        IReadOnlyDictionary<string, string>? routing,
        string? defaultUrl,
        string? topic)
    {
        if (routing is not null && !string.IsNullOrWhiteSpace(topic))
        {
            string? bestKey = null;
            foreach (KeyValuePair<string, string> entry in routing)
            {
                if (string.IsNullOrWhiteSpace(entry.Key) || string.IsNullOrWhiteSpace(entry.Value))
                    continue;

                if (!TopicFilter.Matches(entry.Key, topic))
                    continue;

                if (bestKey is null || TopicFilter.IsMoreSpecific(entry.Key, bestKey))
                    bestKey = entry.Key;
            }

            if (bestKey is not null)
                return routing[bestKey].Trim();
        }

        return string.IsNullOrWhiteSpace(defaultUrl) ? null : defaultUrl.Trim();
    }
}
