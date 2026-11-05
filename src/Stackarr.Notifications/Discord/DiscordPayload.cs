namespace Stackarr.Notifications.Discord;

/// <summary>
/// Discord webhook execute payload. Property names are serialised as snake_case to match the
/// Discord API (username, avatar_url, embeds, ...).
/// </summary>
public sealed class DiscordWebhookPayload
{
    public string? Username { get; set; }
    public string? AvatarUrl { get; set; }
    public List<DiscordEmbed> Embeds { get; set; } = [];
}

/// <summary>Message payload for a bot DM. Webhook-only fields (username/avatar) are omitted.</summary>
public sealed class DiscordMessagePayload
{
    public string? Content { get; set; }
    public List<DiscordEmbed> Embeds { get; set; } = [];
}

public sealed class DiscordEmbed
{
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? Url { get; set; }

    /// <summary>Decimal RGB colour, e.g. 0x2ECC71.</summary>
    public int? Color { get; set; }

    public DateTimeOffset? Timestamp { get; set; }
    public DiscordEmbedAuthor? Author { get; set; }
    public DiscordEmbedThumbnail? Thumbnail { get; set; }
    public DiscordEmbedImage? Image { get; set; }
    public DiscordFooter? Footer { get; set; }
    public List<DiscordEmbedField>? Fields { get; set; }
}

public sealed class DiscordEmbedAuthor
{
    public string Name { get; set; } = "";
    public string? Url { get; set; }
    public string? IconUrl { get; set; }
}

public sealed class DiscordEmbedThumbnail
{
    public string Url { get; set; } = "";
}

public sealed class DiscordEmbedImage
{
    public string Url { get; set; } = "";
}

public sealed class DiscordFooter
{
    public string Text { get; set; } = "";
    public string? IconUrl { get; set; }
}

public sealed class DiscordEmbedField
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
    public bool Inline { get; set; } = true;
}
