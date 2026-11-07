namespace Arrk.Notifications;

/// <summary>
/// Presentation hints for a notification. Channels use these to render richer messages (Discord
/// embeds in particular) while remaining optional: any field left null falls back to the
/// catalogue default for the topic and then to severity.
/// </summary>
public sealed record NotificationStyle
{
    /// <summary>Emoji prefix for the title, e.g. "🛑".</summary>
    public string? Emoji { get; init; }

    /// <summary>Decimal RGB accent colour.</summary>
    public int? Color { get; init; }

    /// <summary>Author line shown above the embed body.</summary>
    public string? AuthorName { get; init; }

    public string? AuthorIconUrl { get; init; }

    /// <summary>Small image in the embed's top-right corner (e.g. a media poster).</summary>
    public string? ThumbnailUrl { get; init; }

    /// <summary>Large image at the bottom of the embed.</summary>
    public string? ImageUrl { get; init; }

    /// <summary>Footer text; defaults to "Arrk • {group}".</summary>
    public string? FooterText { get; init; }

    /// <summary>Overlays non-null values from <paramref name="overrides"/> onto this style.</summary>
    public NotificationStyle Merge(NotificationStyle? overrides) => overrides is null
        ? this
        : new NotificationStyle
        {
            Emoji = overrides.Emoji ?? Emoji,
            Color = overrides.Color ?? Color,
            AuthorName = overrides.AuthorName ?? AuthorName,
            AuthorIconUrl = overrides.AuthorIconUrl ?? AuthorIconUrl,
            ThumbnailUrl = overrides.ThumbnailUrl ?? ThumbnailUrl,
            ImageUrl = overrides.ImageUrl ?? ImageUrl,
            FooterText = overrides.FooterText ?? FooterText
        };
}
