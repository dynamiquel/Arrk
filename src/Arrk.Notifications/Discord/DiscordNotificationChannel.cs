using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Arrk.Auth.Abstractions;
using Arrk.Core.Configuration;
using Arrk.Core.Events;
using Arrk.Core.Notifications;
using Arrk.Notifications.Discord;
using NotificationDiscordConfig = Arrk.Core.Configuration.DiscordConfig;

namespace Arrk.Notifications;

/// <summary>
/// Delivers notifications to Discord as rich, per-topic styled embeds. Broadcasts use the
/// configured webhook; per-user alerts use a bot DM. Both are read from
/// <see cref="NotificationConfig"/> on every send so config hot-reload takes effect immediately.
/// </summary>
public sealed class DiscordNotificationChannel(
    DiscordWebhookClient client,
    DiscordBotClient bot,
    UserNotificationChannelService userChannels,
    NotificationStackInfo stack,
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<NotificationConfig> config,
    NotificationEventRegistry registry,
    ILogger<DiscordNotificationChannel> logger) : INotificationChannel
{
    public const string ChannelName = "discord";

    // Discord embed limits.
    const int MaxTitle = 256;
    const int MaxDescription = 4096;
    const int MaxFieldName = 256;
    const int MaxFieldValue = 1024;
    const int MaxFields = 25;
    const int MaxFooter = 2048;
    const int MaxAuthor = 256;

    public string Name => ChannelName;

    public bool IsConfigured
    {
        get
        {
            NotificationDiscordConfig discord = config.CurrentValue.Discord;
            if (!discord.Enabled)
                return false;

            return WebhookRouting.HasAnyTarget(discord.Routing, discord.WebhookUrl)
                || bot.IsConfigured;
        }
    }

    public bool SupportsDirectDelivery =>
        config.CurrentValue.Discord.Enabled && bot.IsConfigured;

    public Task<NotificationDeliveryResult> SendAsync(NotificationMessage message, CancellationToken ct = default)
    {
        NotificationDiscordConfig discord = config.CurrentValue.Discord;
        if (!discord.Enabled)
            return Task.FromResult(NotificationDeliveryResult.Fail("Discord channel is not configured"));

        if (NotificationFilter.IsExcluded(discord.Exclude, message.Key))
        {
            logger.LogDebug("Discord delivery skipped for {Topic} by exclude filter", message.Key);
            return Task.FromResult(NotificationDeliveryResult.Skip());
        }

        string? webhookUrl = WebhookRouting.Resolve(discord.Routing, discord.WebhookUrl, message.Key);
        if (webhookUrl is null)
        {
            // DM-only configurations should not report a broadcast failure.
            return Task.FromResult(bot.IsConfigured
                ? NotificationDeliveryResult.Skip()
                : NotificationDeliveryResult.Fail("Discord channel is not configured"));
        }

        DiscordWebhookPayload payload = BuildWebhookPayload(discord, message);
        logger.LogDebug("Sending Discord notification {Topic} to webhook", message.Key);
        return client.SendAsync(webhookUrl, payload, ct);
    }

    public async Task<NotificationDeliveryResult> SendToUserAsync(
        int userId,
        NotificationMessage message,
        CancellationToken ct = default)
    {
        if (!config.CurrentValue.Discord.Enabled || !bot.IsConfigured)
            return NotificationDeliveryResult.Skip();

        if (NotificationFilter.IsExcluded(config.CurrentValue.Discord.Exclude, message.Key))
        {
            logger.LogDebug("Discord DM skipped for {Topic} by exclude filter", message.Key);
            return NotificationDeliveryResult.Skip();
        }

        // Direct messages are on by default once Discord is linked and the bot is active. A row
        // with Enabled=false is an explicit opt-out, so only that suppresses delivery.
        UserNotificationChannel? binding = await userChannels.GetAsync(userId, ChannelName, ct);
        if (binding is { Enabled: false })
            return NotificationDeliveryResult.Skip();

        string? discordUserId = await ResolveDiscordUserIdAsync(userId, ct);
        if (string.IsNullOrWhiteSpace(discordUserId))
        {
            logger.LogDebug("Discord DM skipped for user {User}: no linked Discord identity", userId);
            return NotificationDeliveryResult.Skip();
        }

        DiscordMessagePayload payload = BuildMessagePayload(message);
        DiscordDirectMessageResult result = await bot.SendDirectMessageAsync(
            discordUserId, payload, CachedChannelFor(binding?.Destination, discordUserId), ct);

        if (result.Success)
        {
            await userChannels.RecordSuccessAsync(
                userId, ChannelName, CacheKey(discordUserId, result.ChannelId), ct);
            return NotificationDeliveryResult.Ok();
        }

        string error = result.Error ?? "Discord direct message failed.";
        await userChannels.RecordFailureAsync(
            userId, ChannelName, error, clearDestination: result.ChannelMissing, ct);
        return NotificationDeliveryResult.Fail(error);
    }

    /// <summary>Sends a one-off verification message from the account UI.</summary>
    public async Task<NotificationDeliveryResult> SendTestAsync(int userId, CancellationToken ct = default)
    {
        if (!config.CurrentValue.Discord.Enabled || !bot.IsConfigured)
            return NotificationDeliveryResult.Fail("Discord direct messages are not configured.");

        string? discordUserId = await ResolveDiscordUserIdAsync(userId, ct);
        if (string.IsNullOrWhiteSpace(discordUserId))
            return NotificationDeliveryResult.Fail("Link Discord to your Arrk account first.");

        NotificationMessage message = new()
        {
            Key = "system.notifications.test",
            Title = "Arrk direct messages work",
            Body = "You will receive Discord DMs for requests and titles you follow.",
            Severity = EventSeverity.Success,
            Timestamp = DateTime.UtcNow
        };

        UserNotificationChannel? binding = await userChannels.GetAsync(userId, ChannelName, ct);
        DiscordDirectMessageResult result = await bot.SendDirectMessageAsync(
            discordUserId,
            BuildMessagePayload(message),
            CachedChannelFor(binding?.Destination, discordUserId),
            ct);

        if (result.Success)
        {
            await userChannels.RecordSuccessAsync(
                userId, ChannelName, CacheKey(discordUserId, result.ChannelId), ct);
            return NotificationDeliveryResult.Ok();
        }

        string error = result.Error ?? "Discord direct message failed.";
        await userChannels.RecordFailureAsync(
            userId, ChannelName, error, clearDestination: result.ChannelMissing, ct);
        return NotificationDeliveryResult.Fail(error);
    }

    /// <summary>
    /// Cached DM channels are scoped to the Discord identity that created them. If the account is
    /// linked to a different Discord user later, the old channel must not be reused.
    /// </summary>
    static string CacheKey(string discordUserId, string channelId) => $"{discordUserId}:{channelId}";

    static string? CachedChannelFor(string? cached, string discordUserId)
    {
        if (string.IsNullOrWhiteSpace(cached))
            return null;

        string prefix = discordUserId + ":";
        return cached.StartsWith(prefix, StringComparison.Ordinal)
            ? cached[prefix.Length..]
            : null;
    }

    async Task<string?> ResolveDiscordUserIdAsync(int userId, CancellationToken ct)
    {
        // The channel is a singleton; the identity store is scoped and lives in the auth module,
        // which may not be loaded. Resolve it in a short-lived scope per send.
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        IExternalIdentityStore? store = scope.ServiceProvider.GetService<IExternalIdentityStore>();
        if (store is null)
            return null;

        IReadOnlyList<UserLoginInfo> logins = await store.GetExternalLoginsAsync(userId);
        return logins.FirstOrDefault(login =>
            string.Equals(login.LoginProvider, ExternalLoginSchemes.Discord, StringComparison.OrdinalIgnoreCase))
            ?.ProviderKey;
    }

    DiscordWebhookPayload BuildWebhookPayload(NotificationDiscordConfig discord, NotificationMessage message) => new()
    {
        Username = string.IsNullOrWhiteSpace(discord.Username) ? "Arrk" : discord.Username,
        AvatarUrl = NullIfEmpty(discord.AvatarUrl),
        Embeds = [BuildEmbed(message)]
    };

    DiscordMessagePayload BuildMessagePayload(NotificationMessage message) => new()
    {
        Embeds = [BuildEmbed(message)]
    };

    DiscordEmbed BuildEmbed(NotificationMessage message)
    {
        NotificationStyle style = registry.StyleFor(message.Key).Merge(message.Style);

        string title = string.IsNullOrWhiteSpace(style.Emoji)
            ? message.Title
            : $"{style.Emoji} {message.Title}";

        List<DiscordEmbedField>? fields = message.Fields.Count == 0
            ? null
            : message.Fields
                .Take(MaxFields)
                .Select(f => new DiscordEmbedField
                {
                    Name = Truncate(f.Name, MaxFieldName),
                    Value = Truncate(f.Value, MaxFieldValue),
                    Inline = f.Inline
                })
                .ToList();

        string? authorIcon = NullIfEmpty(style.AuthorIconUrl)
            ?? NullIfEmpty(config.CurrentValue.Discord.AvatarUrl);

        string footer = style.FooterText ?? $"{stack.Name} · {registry.GroupOf(message.Key)}";
        string authorName = string.IsNullOrWhiteSpace(style.AuthorName) ||
                            string.Equals(style.AuthorName, "Arrk", StringComparison.Ordinal)
            ? stack.Name
            : style.AuthorName;
        return new DiscordEmbed
        {
            Title = Truncate(title, MaxTitle),
            Description = Truncate(message.Body, MaxDescription),
            Url = stack.AbsoluteUrl(message.Url),
            Color = style.Color ?? SeverityColor(message.Severity),
            Timestamp = ToOffset(message.Timestamp),
            Author = new DiscordEmbedAuthor
            {
                Name = Truncate(authorName, MaxAuthor),
                Url = stack.PublicUrl,
                IconUrl = authorIcon
            },
            // Discord only fetches absolute image URLs, so a relative in-app art path is dropped.
            Thumbnail = AbsoluteOrNull(style.ThumbnailUrl) is { } thumbnail
                ? new DiscordEmbedThumbnail { Url = thumbnail }
                : null,
            Image = AbsoluteOrNull(style.ImageUrl) is { } image
                ? new DiscordEmbedImage { Url = image }
                : null,
            Footer = new DiscordFooter
            {
                Text = Truncate($"{footer} · {stack.Name} · {stack.PublicUrl}", MaxFooter),
                IconUrl = authorIcon
            },
            Fields = fields
        };
    }

    /// <summary>Maps event severity to the Discord embed accent colour (used when no topic colour).</summary>
    public static int SeverityColor(EventSeverity severity) => severity switch
    {
        EventSeverity.Trace => 0x95A5A6,
        EventSeverity.Info => 0x3498DB,
        EventSeverity.Success => 0x2ECC71,
        EventSeverity.Warning => 0xE67E22,
        EventSeverity.Error => 0xE74C3C,
        _ => 0x3498DB
    };

    static DateTimeOffset ToOffset(DateTime timestamp) => timestamp.Kind == DateTimeKind.Unspecified
        ? new DateTimeOffset(DateTime.SpecifyKind(timestamp, DateTimeKind.Utc))
        : new DateTimeOffset(timestamp);

    static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>Discord only fetches absolute images; return the value only when it is a usable URL.</summary>
    static string? AbsoluteOrNull(string? value) =>
        !string.IsNullOrWhiteSpace(value) && IsAbsoluteUrl(value) ? value : null;

    /// <summary>Discord embeds require an absolute URL; a relative in-app deep link must be dropped.</summary>
    static bool IsAbsoluteUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
