using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using FluentValidation;

namespace Arrk.Notifications.Discord;

public class DiscordNotificationConfig
{
    public const string SectionName = "notifications:discord";

    [Description("Enable Discord notifications")]
    public bool Enabled { get; set; } = true;

    [Description("Discord webhook URL for sending notifications")]
    [DataType(DataType.Password)]
    public string WebhookUrl { get; set; } = "";

    [Description("Display name shown on Discord messages")]
    public string Username { get; set; } = "Arrk";

    [Description("Optional avatar URL for the webhook messages")]
    public string AvatarUrl { get; set; } = "";

    [Description("Per-topic webhook URLs. '#'/'*' match any depth and '+' matches one level; the most specific match wins. Blank values fall back to the default URL.")]
    public Dictionary<string, string> Routing { get; set; } = new();

    [Description("Named role to Discord webhook URL, e.g. Admin. Role alerts also reach these endpoints; unmapped roles are not sent to the global webhook. Configure channel permissions yourself if privacy is required.")]
    public Dictionary<string, string> RoleRouting { get; set; } = new();

    [Description("Topic patterns never delivered to Discord. Matching alerts still appear in the in-app inbox.")]
    public List<string> Exclude { get; set; } = [];

    [Description("Per-user direct messages from a Discord bot. Linked users receive DMs unless they opt out.")]
    public DiscordDirectMessageConfig DirectMessages { get; set; } = new();
}

public class DiscordDirectMessageConfig
{
    [Description("Enable Discord direct messages. The bot must share a server with each recipient.")]
    public bool Enabled { get; set; } = false;

    [Description("Discord bot token from the application's Bot page. Keep this secret.")]
    [DataType(DataType.Password)]
    public string BotToken { get; set; } = "";

    [Description("Discord application id, used to build the bot invite link.")]
    public string ApplicationId { get; set; } = "";

    [Description("Discord API base URL. Override only for testing.")]
    public string ApiBaseUrl { get; set; } = "https://discord.com/api/v10";
}

public sealed class DiscordNotificationConfigValidator : AbstractValidator<DiscordNotificationConfig>
{
    public DiscordNotificationConfigValidator()
    {
        RuleFor(x => x.WebhookUrl).Must(IsBlankOrHttpUrl)
            .WithMessage("Discord webhook URL must be an absolute http(s) URL.");
        RuleForEach(x => x.Routing).Must(entry => IsBlankOrHttpUrl(entry.Value))
            .WithMessage("Discord per-topic webhook URLs must be absolute http(s) URLs.");
        RuleForEach(x => x.RoleRouting).Must(entry => IsBlankOrHttpUrl(entry.Value))
            .WithMessage("Discord role webhook URLs must be absolute http(s) URLs.");
        When(x => x.DirectMessages.Enabled, () =>
        {
            RuleFor(x => x.DirectMessages.BotToken).NotEmpty()
                .WithMessage("A Discord bot token is required when direct messages are enabled.");
            RuleFor(x => x.DirectMessages.ApiBaseUrl).NotEmpty().Must(IsBlankOrHttpUrl)
                .WithMessage("The Discord API base URL must be an absolute http(s) URL.");
        });
    }

    static bool IsBlankOrHttpUrl(string? value) => string.IsNullOrWhiteSpace(value)
        || (Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) && uri.Scheme is "http" or "https");
}
