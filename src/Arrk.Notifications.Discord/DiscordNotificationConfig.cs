using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using FluentValidation;

namespace Arrk.Notifications.Discord;

public class DiscordNotificationConfig
{
    public const string SectionName = "notifications:discord";

    [Description("Enable Discord notifications")]
    public bool Enabled { get; set; } = true;

    [Description("Display name shown on Discord messages")]
    public string Username { get; set; } = "Arrk";

    [Description("Optional avatar URL for the webhook messages")]
    public string AvatarUrl { get; set; } = "";

    [Description("Shared webhook routes by topic and source roles. Every matching route receives the alert. Configure Discord channel permissions yourself if privacy is required.")]
    public List<NotificationRoute> Routing { get; set; } = [];

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
        RuleForEach(x => x.Routing).SetValidator(new NotificationRouteValidator());
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
