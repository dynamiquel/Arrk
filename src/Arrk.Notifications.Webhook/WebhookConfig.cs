using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using FluentValidation;

namespace Arrk.Notifications.Webhook;

public class WebhookConfig
{
    public const string SectionName = "notifications:webhook";

    [Description("Enable generic JSON webhook notifications")]
    public bool Enabled { get; set; } = false;

    [Description("Optional raw Authorization header value, e.g. 'Bearer abc123'")]
    [DataType(DataType.Password)]
    public string? AuthHeader { get; set; }

    [Description("Shared webhook routes by topic and source roles. Every matching route receives the alert. Configure access control at the destination yourself if privacy is required.")]
    public List<NotificationRoute> Routing { get; set; } = [];

    [Description("Topic patterns never delivered to this webhook. Matching alerts still appear in the in-app inbox.")]
    public List<string> Exclude { get; set; } = [];
}

public sealed class WebhookConfigValidator : AbstractValidator<WebhookConfig>
{
    public WebhookConfigValidator()
    {
        RuleForEach(x => x.Routing).SetValidator(new NotificationRouteValidator());
    }
}
