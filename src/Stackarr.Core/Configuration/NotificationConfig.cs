using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace Stackarr.Core.Configuration;

public class NotificationConfig
{
    [Description("Discord webhook channel")]
    public DiscordConfig Discord { get; set; } = new();

    [Description("Generic JSON webhook channel (universal; works with Apprise and similar)")]
    public WebhookConfig Webhook { get; set; } = new();

    [Description("Days to keep notifications in the in-app inbox (0 = keep forever)")]
    public int RetentionDays { get; set; } = 90;

    [Description("Show running background jobs as live activity in the notification inbox. Off by default because recurring jobs are noisy; their start/finish/failure events still appear on the All Events page.")]
    public bool ShowJobActivity { get; set; } = false;

    [Description("Enable/disable notification topics or groups. Keys are hierarchical topic patterns such as 'system.health', 'downloads.torrent', 'media.music.#' or 'downloads.*'; '#'/'*' match any depth and '+' matches one level. The most specific match wins; unset topics use their catalogue default.")]
    public Dictionary<string, bool> Topics { get; set; } = new();
}

public class DiscordConfig
{
    [Description("Enable the Discord channel")]
    public bool Enabled { get; set; } = true;

    [Description("Discord webhook URL for sending notifications")]
    [DataType(DataType.Password)]
    public string WebhookUrl { get; set; } = "";

    [Description("Display name shown on Discord messages")]
    public string Username { get; set; } = "Stackarr";

    [Description("Optional avatar URL for the webhook messages")]
    public string AvatarUrl { get; set; } = "";

    [Description("Per-category webhook URLs keyed by notification topic pattern (e.g. 'media.tv.#', 'media.requests', 'downloads.*'). '#'/'*' match any depth and '+' matches one level; the most specific match wins; unset topics fall back to the default URL. Leave a value empty to keep using the default.")]
    public Dictionary<string, string> Routing { get; set; } = new();

    [Description("Topic patterns that are never delivered to Discord (same syntax as routing, e.g. 'system.health.network.speedtest.completed' or 'downloads.#'). Matching alerts still appear in the in-app inbox; this only suppresses Discord delivery.")]
    public List<string> Exclude { get; set; } = [];

    [Description("Per-user direct messages from a Discord bot. Requires users to link Discord and opt in from their account.")]
    public DiscordDirectMessageConfig DirectMessages { get; set; } = new();
}

public class DiscordDirectMessageConfig
{
    [Description("Enable Discord direct messages. The bot must share a server with each recipient.")]
    public bool Enabled { get; set; } = false;

    [Description("Discord bot token from the application's Bot page. Keep this secret.")]
    [DataType(DataType.Password)]
    public string BotToken { get; set; } = "";

    [Description("Discord application id, used to build the bot invite link shown on the notifications page.")]
    public string ApplicationId { get; set; } = "";

    [Description("Discord API base URL. Override only for testing.")]
    public string ApiBaseUrl { get; set; } = "https://discord.com/api/v10";
}

public class WebhookConfig
{
    [Description("Enable the generic webhook channel")]
    public bool Enabled { get; set; } = false;

    [Description("URL that receives a JSON POST for each notification")]
    public string Url { get; set; } = "";

    [Description("Optional raw Authorization header value, e.g. 'Bearer abc123'")]
    [DataType(DataType.Password)]
    public string? AuthHeader { get; set; }

    [Description("Per-category webhook URLs keyed by notification topic pattern (e.g. 'media.tv.#', 'media.requests', 'downloads.*'). '#'/'*' match any depth and '+' matches one level; the most specific match wins; unset topics fall back to the default URL. Leave a value empty to keep using the default.")]
    public Dictionary<string, string> Routing { get; set; } = new();

    [Description("Topic patterns that are never delivered to this webhook (same syntax as routing, e.g. 'system.health.network.speedtest.completed' or 'downloads.#'). Matching alerts still appear in the in-app inbox; this only suppresses webhook delivery.")]
    public List<string> Exclude { get; set; } = [];
}
